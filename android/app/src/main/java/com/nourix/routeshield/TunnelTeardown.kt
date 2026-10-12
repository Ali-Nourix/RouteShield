package com.nourix.routeshield
/** TUN/physical monitor must close before native shutdown can wait for packet readers. */
object TunnelTeardown {
    fun close(tun:()->Unit,platform:()->Unit,coreService:()->Unit,coreServer:()->Unit,telemetry:()->Unit):Boolean {
        var okay=true
        for(action in listOf(tun,platform,coreService,coreServer,telemetry)) runCatching(action).onFailure {okay=false}
        return okay
    }
}
