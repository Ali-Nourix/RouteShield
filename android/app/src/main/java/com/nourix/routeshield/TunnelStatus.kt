package com.nourix.routeshield
import java.util.concurrent.CopyOnWriteArraySet

data class TunnelSnapshot(val stage:String="idle", val latencyMs:Long?=null, val up:Long?=null, val down:Long?=null,
    val totalUp:Long?=null, val totalDown:Long?=null, val exitIp:String?=null, val selected:String?=null,
    val delays:Map<String,Int> = emptyMap(), val alwaysOn:Boolean?=null, val lockdown:Boolean?=null, val latencyHistory:List<Long?> = emptyList()) {
    val running get()=stage in setOf("starting","ready","online","waiting","degraded","stopping")
}
object TunnelStatus {
    @Volatile var value=TunnelSnapshot(); private set
    val listeners=CopyOnWriteArraySet<(TunnelSnapshot)->Unit>()
    @Synchronized fun update(stage:String, latency:Long?=null) {
        require(latency==null || latency>=0)
        if(value.stage=="stopping" && stage in setOf("starting","ready","online","waiting","degraded")) return
        val history=when {
            stage=="starting"->emptyList()
            stage=="online" && latency!=null->(value.latencyHistory+latency).takeLast(12)
            stage in setOf("waiting","degraded")->(value.latencyHistory+listOf<Long?>(null)).takeLast(12)
            else->value.latencyHistory
        }
        value=if(stage=="idle") TunnelSnapshot() else value.copy(stage=stage,latencyMs=latency,latencyHistory=history)
        listeners.forEach { it(value) }
    }
    @Synchronized fun protection(alwaysOn:Boolean,lockdown:Boolean) {
        value=value.copy(alwaysOn=alwaysOn,lockdown=lockdown); listeners.forEach {it(value)}
    }
    @Synchronized fun metrics(up:Long?,down:Long?,totalUp:Long?,totalDown:Long?) {
        value=value.copy(up=up,down=down,totalUp=totalUp,totalDown=totalDown); listeners.forEach { it(value) }
    }
    @Synchronized fun group(selected:String?,delays:Map<String,Int>) { value=value.copy(selected=selected,delays=delays); listeners.forEach { it(value) } }
    @Synchronized fun clearExit() {value=value.copy(exitIp=null);listeners.forEach {it(value)}}
    @Synchronized fun exit(ip:String) { value=value.copy(exitIp=ip); listeners.forEach { it(value) } }
}
