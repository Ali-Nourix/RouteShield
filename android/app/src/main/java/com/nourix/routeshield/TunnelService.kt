package com.nourix.routeshield

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import io.nekohasekai.libbox.*
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class TunnelService : VpnService(), CommandServerHandler {
    companion object { const val STOP = "com.nourix.routeshield.STOP"; const val CHANNEL = "vpn"; const val ID = 31; private val lifecycle = Executors.newSingleThreadScheduledExecutor()
        @Volatile private var active: TunnelService? = null
        fun requestStop(): Boolean = active?.let { it.shutdown(); true } ?: false
        fun refreshProtection() { active?.readProtection() }
        fun selectionChanged(isAutomatic:Boolean) { active?.automatic=isAutomatic }
    }
    private val executor = lifecycle
    private var healthExecutor = Executors.newSingleThreadScheduledExecutor()
    @Volatile private var activeProbe: HttpURLConnection? = null
    private var server: CommandServer? = null
    private var telemetry: CommandClient? = null
    private var lastExitCheck = 0L
    private var platform: NativePlatform? = null
    private val retestBudget = RetestBudget()
    private var healthGate = HealthGate()
    @Volatile private var automatic = false
    @Volatile private var shutdownComplete = false
    private var health: java.util.concurrent.ScheduledFuture<*>? = null
    @Volatile var closing = false; private set
    @Volatile var tun: ParcelFileDescriptor? = null
    override fun onCreate() { super.onCreate(); active=this }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == STOP) { shutdown(); return START_NOT_STICKY }
        synchronized(this) {
            if(closing && !shutdownComplete) return START_NOT_STICKY
            if(closing) { closing=false; shutdownComplete=false; healthExecutor=Executors.newSingleThreadScheduledExecutor() }
        }
        if (prepare(this) != null) { TunnelStatus.update("permission"); stopSelf(); return START_NOT_STICKY }
        showNotification("starting") // foreground deadline, before native initialization
        executor.execute { if (server == null && !closing) startTunnel() }
        return START_NOT_STICKY
    }
    private fun startTunnel() {
        TunnelStatus.update("starting")
        try {
            val saved = ProfileStore(this).load()
            healthGate=HealthGate(saved.optBoolean("preferStability",true))
            automatic = saved.optInt("selected", 0) == 0 && ProfileConfig.activeNodes(saved).length() > 1
            val config = ProfileConfig.runtime(ProfileConfig.activeNodes(saved), saved.optInt("selected", 0), saved)
            val directory = java.io.File(filesDir, "engine").apply { mkdirs() }
            Libbox.setup(SetupOptions().apply {
                basePath = directory.path; workingPath = directory.path; tempPath = cacheDir.path
                debug = false; logMaxLines = 0; commandServerSecret = UUID.randomUUID().toString()
                oomKillerDisabled = true; powerReportEnabled = false
            })
            check(!closing)
            Libbox.checkConfig(config)
            check(!closing)
            val adapter = NativePlatform(this).also { platform = it; it.start() }
            val command = CommandServer(this, adapter).also { server = it }
            command.start(); command.startOrReloadService(config, OverrideOptions())
            val options=CommandClientOptions().apply { statusInterval=1_000_000_000L; addCommand(Libbox.CommandStatus); addCommand(Libbox.CommandGroup) }
            telemetry=CommandClient(object : CommandClientHandler {
                override fun connected() {} ; override fun disconnected(message:String?) {}
                override fun clearLogs() {} ; override fun setDefaultLogLevel(level:Int) {}
                override fun initializeClashMode(modes:StringIterator?,current:String?) {} ; override fun updateClashMode(mode:String?) {}
                override fun writeLogs(logs:LogIterator?) {} ; override fun writeConnectionEvents(events:ConnectionEvents?) {}
                override fun writeOutbounds(items:OutboundGroupItemIterator?) {}
                override fun writeStatus(status:StatusMessage) {
                    if(!closing && status.trafficAvailable) TunnelStatus.metrics(status.uplink,status.downlink,status.uplinkTotal,status.downlinkTotal)
                }
                override fun writeGroups(groups:OutboundGroupIterator) {
                    val selected=mutableMapOf<String,String>(); val delays=mutableMapOf<String,Int>()
                    while(groups.hasNext()) {
                        val group=groups.next(); selected[group.tag]=group.selected
                        val items=group.items
                        while(items.hasNext()) { val item=items.next(); if(item.getURLTestDelay()>0) delays[item.tag]=item.getURLTestDelay() }
                    }
                    val route=selected["proxy"]
                    if(!closing) TunnelStatus.group(if(route=="auto") selected["auto"] else route,delays)
                }
            },options).also { it.connect() }
            check(!closing)
            readProtection()
            TunnelStatus.update("ready"); showNotification("ready")
            if(automatic) runCatching { Libbox.newStandaloneCommandClient().urlTest("auto") }
            health = healthExecutor.scheduleWithFixedDelay({ checkHealth() }, 0, 15, TimeUnit.SECONDS)
        } catch (e: Exception) {
            // Do not put native errors/configs/keys into logcat or notifications.
            if(!closing) TunnelStatus.update("error"); shutdown()
        }
    }
    private fun readProtection() {
        if(!closing && Build.VERSION.SDK_INT>=29) runCatching { TunnelStatus.protection(isAlwaysOn,isLockdownEnabled) }
    }
    private fun checkHealth() {
        if (closing) return
        readProtection()
        var connection: HttpURLConnection? = null
        try {
            val started = System.nanoTime()
            // A probe-provider outage is not enough evidence to churn the VPN route.
            var success=false
            for(endpoint in listOf("https://www.gstatic.com/generate_204","https://cp.cloudflare.com/generate_204")) {
                if(closing) return
                val probe=URL(endpoint).openConnection() as HttpURLConnection; connection=probe; activeProbe=probe
                probe.connectTimeout=3500; probe.readTimeout=3500
                probe.instanceFollowRedirects=false; probe.useCaches=false
                success=runCatching {probe.responseCode==204}.getOrDefault(false)
                probe.disconnect();activeProbe=null
                if(success) break
            }
            check(success)
            val latency = (System.nanoTime() - started) / 1_000_000
            if (!closing) { retestBudget.healthy(); healthGate.success(); TunnelStatus.update("online", latency); showNotification("online") }
            val now=android.os.SystemClock.elapsedRealtime()
            if(!closing && now-lastExitCheck>60_000) {
                lastExitCheck=now
                runCatching {
                    val ip=URL("https://api.ipify.org?format=json").openConnection() as HttpURLConnection
                    try {
                        activeProbe=ip
                        ip.connectTimeout=4000; ip.readTimeout=4000; ip.instanceFollowRedirects=false; ip.useCaches=false
                        check(ip.responseCode==200)
                        val body=ip.inputStream.use { stream -> val bytes=java.io.ByteArrayOutputStream(); val buffer=ByteArray(128); while(true) { val n=stream.read(buffer); if(n<0) break; check(bytes.size()+n<=256); bytes.write(buffer,0,n) }; String(bytes.toByteArray(),Charsets.UTF_8) }
                        val address=org.json.JSONObject(body).getString("ip")
                        if(!closing && address.length<=45 && address.all { it.isDigit() || it in ".:abcdefABCDEF" }) TunnelStatus.exit(address)
                    } finally { ip.disconnect(); activeProbe=null }
                }
            }
        } catch (e: Exception) {
            val recover=healthGate.failureNeedsRecovery()
            if (!closing) { val stage=if(recover) "waiting" else "degraded"; TunnelStatus.update(stage); showNotification(stage) }
            // Immediately remeasure the automatic group on an outage, then back off. Native
            // urltest keeps healthy existing sessions rather than unconditionally closing them.
            if (!closing && recover && automatic && retestBudget.permit(android.os.SystemClock.elapsedRealtime())) {
                runCatching { Libbox.newStandaloneCommandClient().urlTest("auto") }
            }
            // Keep the TUN up: loss of connectivity is not a request to send traffic directly.
        } finally { connection?.disconnect(); activeProbe=null }
    }
    private fun showNotification(stage: String) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "VPN", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, TunnelService::class.java).setAction(STOP), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val text = getString(statusResource(stage))
        val notification = Notification.Builder(this, CHANNEL).setContentTitle("RouteShield").setContentText(text)
            .setSmallIcon(R.drawable.ic_notification).setContentIntent(open).setOngoing(true)
            .addAction(Notification.Action.Builder(null, getString(R.string.disconnect), stop).build()).build()
        if (Build.VERSION.SDK_INT >= 34) startForeground(ID, notification, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED)
        else startForeground(ID, notification)
    }
    /** stopService alone does not destroy a service still bound by Android's VPN manager. */
    @Synchronized private fun shutdown() {
        if(closing) return
        closing = true
        if(TunnelStatus.value.stage !in setOf("error","permission")) TunnelStatus.update("stopping")
        health?.cancel(true); activeProbe?.disconnect(); healthExecutor.shutdownNow()
        val descriptor=tun; tun=null; runCatching { descriptor?.close() }
        stopForeground(STOP_FOREGROUND_REMOVE)
        executor.execute {
            val okay=TunnelTeardown.close(
                tun={ synchronized(this) { val late=tun; tun=null; late?.close() } },
                platform={ platform?.close(); platform=null },
                coreService={ server?.closeService() },
                coreServer={ server?.close(); server=null },
                telemetry={ telemetry?.disconnect(); telemetry=null })
            shutdownComplete=true
            if (TunnelStatus.value.stage !in setOf("error","permission")) TunnelStatus.update(if(okay) "idle" else "error")
            stopSelf()
        }
    }
    @Synchronized fun attachTun(descriptor:ParcelFileDescriptor):Int {
        if(closing) { descriptor.close(); error("VPN startup was cancelled") }
        tun?.close(); tun=descriptor; return descriptor.fd
    }
    override fun onDestroy() { shutdown(); if(active===this) active=null; super.onDestroy() }
    override fun onRevoke() { shutdown() }
    override fun serviceStop() { shutdown() }
    override fun serviceReload() { /* no remote reload path; policy changes require an explicit restart */ }
    override fun getSystemProxyStatus() = SystemProxyStatus()
    override fun setSystemProxyEnabled(enabled: Boolean) { check(!enabled) { "System proxy disabled" } }
    override fun connectSSHAgent() = -1
    override fun triggerNativeCrash() { error("Native crash trigger disabled") }
    override fun writeDebugMessage(message: String?) { /* intentionally not persisted */ }
    private fun statusResource(stage: String) = when(stage) {
        "starting" -> R.string.starting; "ready" -> R.string.ready; "online" -> R.string.online
        "waiting" -> R.string.waiting; "degraded" -> R.string.degraded; "stopping" -> R.string.stopping; else -> R.string.idle
    }
}
