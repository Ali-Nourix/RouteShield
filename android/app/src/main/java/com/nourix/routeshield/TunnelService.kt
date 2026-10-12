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
    companion object { const val STOP = "com.nourix.routeshield.STOP"; const val CHANNEL = "vpn"; const val ID = 31; private val lifecycle = Executors.newSingleThreadScheduledExecutor() }
    private val executor = lifecycle
    private val healthExecutor = Executors.newSingleThreadScheduledExecutor()
    @Volatile private var activeProbe: HttpURLConnection? = null
    private var server: CommandServer? = null
    private var telemetry: CommandClient? = null
    private var lastExitCheck = 0L
    private var platform: NativePlatform? = null
    private val retestBudget = RetestBudget()
    private var automatic = false
    private var health: java.util.concurrent.ScheduledFuture<*>? = null
    @Volatile var closing = false; private set
    @Volatile var tun: ParcelFileDescriptor? = null
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == STOP) { stopSelf(); return START_NOT_STICKY }
        if (prepare(this) != null) { TunnelStatus.update("permission"); stopSelf(); return START_NOT_STICKY }
        showNotification("starting") // foreground deadline, before native initialization
        executor.execute { if (server == null && !closing) startTunnel() }
        return START_NOT_STICKY
    }
    private fun startTunnel() {
        TunnelStatus.update("starting")
        try {
            val saved = ProfileStore(this).load()
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
                    while(groups.hasNext()) {
                        val group=groups.next(); if(group.tag!="proxy") continue
                        val delays=mutableMapOf<String,Int>(); val items=group.items
                        while(items.hasNext()) { val item=items.next(); if(item.getURLTestDelay()>0) delays[item.tag]=item.getURLTestDelay() }
                        if(!closing) TunnelStatus.group(group.selected,delays)
                    }
                }
            },options).also { it.connect() }
            check(!closing)
            TunnelStatus.update("ready"); showNotification("ready")
            health = healthExecutor.scheduleWithFixedDelay({ checkHealth() }, 0, 15, TimeUnit.SECONDS)
        } catch (e: Exception) {
            // Do not put native errors/configs/keys into logcat or notifications.
            if(!closing) TunnelStatus.update("error"); stopSelf()
        }
    }
    private fun checkHealth() {
        if (closing) return
        var connection: HttpURLConnection? = null
        try {
            val started = System.nanoTime()
            connection = URL("https://www.gstatic.com/generate_204").openConnection() as HttpURLConnection
            activeProbe = connection
            connection.connectTimeout = 5000; connection.readTimeout = 5000
            connection.instanceFollowRedirects = false; connection.useCaches = false
            check(connection.responseCode == 204)
            val latency = (System.nanoTime() - started) / 1_000_000
            if (!closing) { retestBudget.healthy(); TunnelStatus.update("online", latency); showNotification("online") }
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
            if (!closing) { TunnelStatus.update("waiting"); showNotification("waiting") }
            // Immediately remeasure the automatic group on an outage, then back off. Native
            // urltest keeps healthy existing sessions rather than unconditionally closing them.
            if (!closing && automatic && retestBudget.permit(android.os.SystemClock.elapsedRealtime())) {
                runCatching { Libbox.newStandaloneCommandClient().urlTest("proxy") }
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
    override fun onDestroy() {
        closing = true
        if(TunnelStatus.value.stage !in setOf("error","permission")) TunnelStatus.update("stopping")
        health?.cancel(true); activeProbe?.disconnect(); healthExecutor.shutdownNow()
        // Unblock the native packet reader BEFORE closeService/telemetry can wait on it.
        val descriptor=tun; tun=null; runCatching { descriptor?.close() }
        executor.execute {
            val okay=TunnelTeardown.close(
                tun={ val late=tun; tun=null; late?.close() },
                platform={ platform?.close(); platform=null },
                coreService={ server?.closeService() },
                coreServer={ server?.close(); server=null },
                telemetry={ telemetry?.disconnect(); telemetry=null })
            if (TunnelStatus.value.stage !in setOf("error","permission")) TunnelStatus.update(if(okay) "idle" else "error")
        }
        stopForeground(STOP_FOREGROUND_REMOVE); super.onDestroy()
    }
    override fun onRevoke() { stopSelf() }
    override fun serviceStop() { stopSelf() }
    override fun serviceReload() { /* no remote reload path; policy changes require an explicit restart */ }
    override fun getSystemProxyStatus() = SystemProxyStatus()
    override fun setSystemProxyEnabled(enabled: Boolean) { check(!enabled) { "System proxy disabled" } }
    override fun connectSSHAgent() = -1
    override fun triggerNativeCrash() { error("Native crash trigger disabled") }
    override fun writeDebugMessage(message: String?) { /* intentionally not persisted */ }
    private fun statusResource(stage: String) = when(stage) {
        "starting" -> R.string.starting; "ready" -> R.string.ready; "online" -> R.string.online
        "waiting" -> R.string.waiting; "stopping" -> R.string.stopping; else -> R.string.idle
    }
}
