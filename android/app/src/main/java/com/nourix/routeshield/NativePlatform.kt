package com.nourix.routeshield

import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.VpnService
import android.os.Build
import android.os.Process
import android.system.OsConstants
import io.nekohasekai.libbox.*
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetSocketAddress
import java.net.NetworkInterface as JavaInterface
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Narrow platform adapter: no root, shell, SSH, bridge or remote management capabilities. */
class NativePlatform(private val vpn: TunnelService) : PlatformInterface {
    private val connectivity = vpn.getSystemService(ConnectivityManager::class.java)
    @Volatile var underlying: Network? = null; private set
    private var listener: InterfaceUpdateListener? = null
    private val dnsExecutor = Executors.newFixedThreadPool(2)
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) { updateNetwork() }
        override fun onLost(network: Network) { updateNetwork() }
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) { updateNetwork() }
        override fun onLinkPropertiesChanged(network: Network, properties: android.net.LinkProperties) { updateNetwork() }
    }
    fun start() {
        connectivity.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), callback)
        updateNetwork()
    }
    fun close() { runCatching { connectivity.unregisterNetworkCallback(callback) }; dnsExecutor.shutdownNow(); listener = null }
    private fun updateNetwork() {
        val networks = connectivity.allNetworks.filter { network -> connectivity.getNetworkCapabilities(network)?.let {
            it.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) && it.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        } == true }
        val active = connectivity.activeNetwork.takeIf { it in networks }
        underlying = active ?: networks.sortedByDescending { connectivity.getNetworkCapabilities(it)?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) == true }.firstOrNull()
        vpn.setUnderlyingNetworks(underlying?.let { arrayOf(it) })
        val name = underlying?.let { connectivity.getLinkProperties(it)?.interfaceName }
        val iface = name?.let { runCatching { JavaInterface.getByName(it) }.getOrNull() }
        listener?.updateDefaultInterface(name ?: "", iface?.index ?: -1, false, false)
    }
    override fun startDefaultInterfaceMonitor(value: InterfaceUpdateListener) { listener = value; updateNetwork() }
    override fun closeDefaultInterfaceMonitor(value: InterfaceUpdateListener) { listener = null }
    override fun usePlatformAutoDetectInterfaceControl() = true
    override fun autoDetectInterfaceControl(fd: Int) { check(vpn.protect(fd)) { "Cannot protect engine socket" } }
    override fun useProcFS() = Build.VERSION.SDK_INT < 29
    override fun findConnectionOwner(protocol: Int, source: String, sourcePort: Int, destination: String, destinationPort: Int): ConnectionOwner {
        if (Build.VERSION.SDK_INT < 29) error("Process lookup unavailable")
        val uid = connectivity.getConnectionOwnerUid(protocol, InetSocketAddress(source, sourcePort), InetSocketAddress(destination, destinationPort))
        check(uid != Process.INVALID_UID) { "Unknown connection owner" }
        val packages = vpn.packageManager.getPackagesForUid(uid)?.toList().orEmpty()
        return ConnectionOwner().apply { userId = uid; userName = packages.firstOrNull().orEmpty(); setAndroidPackageNames(Strings(packages)) }
    }
    override fun openTun(options: TunOptions): Int {
        check(!vpn.closing) { "VPN startup was cancelled" }
        check(VpnService.prepare(vpn) == null) { "VPN permission required" }
        val builder = vpn.Builder().setSession("RouteShield").setMtu(options.getMTU())
        if (Build.VERSION.SDK_INT >= 29) builder.setMetered(false)
        val v4 = options.getInet4Address(); while (v4.hasNext()) { val a = v4.next(); builder.addAddress(a.address(), a.prefix()) }
        val v6 = options.getInet6Address(); while (v6.hasNext()) { val a = v6.next(); builder.addAddress(a.address(), a.prefix()) }
        // Capture both families. Unsupported IPv6 fails in the tunnel rather than bypassing it.
        builder.addRoute("0.0.0.0", 0).addRoute("::", 0).addDnsServer("172.19.0.2")
        val saved = ProfileStore(vpn).load()
        val apps = saved.optJSONArray("apps") ?: org.json.JSONArray()
        require(saved.optString("mode","all") in setOf("all","include","exclude")) { "Unknown app routing policy" }
        if(saved.optString("mode","all")=="include") {
            require(apps.length()>0) { "Select at least one application" }
            for(i in 0 until apps.length()) builder.addAllowedApplication(apps.getString(i))
            builder.addAllowedApplication(vpn.packageName)
        } else {
            @Suppress("DEPRECATION") val installed= vpn.packageManager.queryIntentActivities(
                android.content.Intent(android.content.Intent.ACTION_MAIN).addCategory(android.content.Intent.CATEGORY_LAUNCHER),0)
                .map {it.activityInfo.packageName}.toSet()
            for(pkg in AppRoutingPolicy.excluded(saved,installed,vpn.packageName)) builder.addDisallowedApplication(pkg)
        }
        underlying?.let { builder.setUnderlyingNetworks(arrayOf(it)) }
        val descriptor = builder.establish() ?: error("TUN establishment failed")
        if(vpn.closing) { descriptor.close(); error("VPN startup was cancelled") }
        vpn.tun?.close(); vpn.tun = descriptor
        return descriptor.fd // Keep ownership until shutdown; never detach and leak the FD.
    }
    override fun getInterfaces(): NetworkInterfaceIterator {
        val values = connectivity.allNetworks.mapNotNull { network ->
            val properties = connectivity.getLinkProperties(network) ?: return@mapNotNull null
            val caps = connectivity.getNetworkCapabilities(network) ?: return@mapNotNull null
            if (!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)) return@mapNotNull null
            val iface = runCatching { JavaInterface.getByName(properties.interfaceName) }.getOrNull() ?: return@mapNotNull null
            io.nekohasekai.libbox.NetworkInterface().apply {
                name = iface.name; index = iface.index; setMTU(iface.mtu)
                flags = OsConstants.IFF_UP or OsConstants.IFF_RUNNING
                type = when { caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> Libbox.InterfaceTypeWIFI
                    caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> Libbox.InterfaceTypeCellular
                    else -> Libbox.InterfaceTypeEthernet }
                addresses = Strings(properties.linkAddresses.map { "${it.address.hostAddress}/${it.prefixLength}" })
                setDNSServer(Strings(properties.dnsServers.mapNotNull { it.hostAddress }))
                gateway = Strings(properties.routes.filter { it.isDefaultRoute }.mapNotNull { it.gateway?.hostAddress })
                metered = !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
            }
        }
        val iterator = values.iterator()
        return object : NetworkInterfaceIterator { override fun hasNext() = iterator.hasNext(); override fun next() = iterator.next() }
    }
    override fun localDNSTransport(): LocalDNSTransport = object : LocalDNSTransport {
        override fun raw() = false
        override fun exchange(ctx: ExchangeContext, message: ByteArray) { error("Raw DNS transport not enabled") }
        override fun lookup(ctx: ExchangeContext, family: String, domain: String) {
            val network = underlying ?: error("No physical network")
            val future = dnsExecutor.submit<List<String>> {
                network.getAllByName(domain).filter { family != "ip4" || it is Inet4Address }
                    .filter { family != "ip6" || it is Inet6Address }.mapNotNull { it.hostAddress }
            }
            ctx.onCancel { future.cancel(true) }
            try { ctx.success(future.get(5, TimeUnit.SECONDS).joinToString("\n")) }
            catch (e: Exception) { future.cancel(true); ctx.errorCode(3) }
        }
    }
    override fun underNetworkExtension() = false
    override fun includeAllNetworks() = false
    override fun clearDNSCache() {}
    override fun readWIFIState(): WIFIState? = null
    override fun registerMyInterface(name: String) {}
    override fun usePlatformShell() = false
    override fun usePlatformBridge() = false
    override fun checkPlatformShell() { error("Shell access disabled") }
    override fun openShellSession(user: PlatformUser, command: String, env: StringIterator, term: String, rows: Int, cols: Int): ShellSession = error("Shell access disabled")
    override fun createBridge(options: BridgeOptions): BridgeSession = error("Bridge access disabled")
    override fun lookupUser(name: String): PlatformUser = error("User lookup disabled")
    override fun lookupSFTPServer(): String = error("SFTP disabled")
    override fun readSystemSSHHostKey(): String = error("SSH disabled")
    override fun tailscaleHostname() = "RouteShield"
    override fun startNeighborMonitor(listener: NeighborUpdateListener) { error("Neighbor monitor disabled") }
    override fun closeNeighborMonitor(listener: NeighborUpdateListener) {}
    override fun sendNotification(notification: io.nekohasekai.libbox.Notification) {}
    override fun cancelNotification(identifier: String, id: Int) {}
    class Strings(private val values: List<String>) : StringIterator {
        private val iterator = values.iterator()
        override fun hasNext() = iterator.hasNext()
        override fun next() = iterator.next()
        override fun len() = values.size
    }
}
