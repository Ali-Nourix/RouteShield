package com.nourix.routeshield
import java.util.concurrent.CopyOnWriteArraySet

data class TunnelSnapshot(val stage:String="idle", val latencyMs:Long?=null, val up:Long?=null, val down:Long?=null,
    val totalUp:Long?=null, val totalDown:Long?=null, val exitIp:String?=null, val selected:String?=null,
    val delays:Map<String,Int> = emptyMap()) {
    val running get()=stage in setOf("starting","ready","online","waiting")
}
object TunnelStatus {
    @Volatile var value=TunnelSnapshot(); private set
    val listeners=CopyOnWriteArraySet<(TunnelSnapshot)->Unit>()
    @Synchronized fun update(stage:String, latency:Long?=null) {
        value=if(stage=="idle") TunnelSnapshot() else value.copy(stage=stage,latencyMs=latency)
        listeners.forEach { it(value) }
    }
    @Synchronized fun metrics(up:Long?,down:Long?,totalUp:Long?,totalDown:Long?) {
        value=value.copy(up=up,down=down,totalUp=totalUp,totalDown=totalDown); listeners.forEach { it(value) }
    }
    @Synchronized fun group(selected:String?,delays:Map<String,Int>) { value=value.copy(selected=selected,delays=delays); listeners.forEach { it(value) } }
    @Synchronized fun exit(ip:String) { value=value.copy(exitIp=ip); listeners.forEach { it(value) } }
}
