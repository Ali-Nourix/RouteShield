package com.nourix.routeshield
import android.os.ParcelFileDescriptor
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
/** Regression: closing must not wait for Android to unbind and call onDestroy. No JNI/traffic claim. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk=[35])
class BoundVpnShutdownTest {
    @org.junit.Before fun reset() {TunnelStatus.update("idle")}
    @Test fun explicit_stop_closes_tun_while_service_is_still_alive() {
        val controller=Robolectric.buildService(TunnelService::class.java).create()
        val service=controller.get();val pipe=ParcelFileDescriptor.createPipe()
        service.attachTun(pipe[0]);TunnelStatus.update("ready")
        assertTrue(TunnelService.requestStop());assertTrue(service.closing);assertNull(service.tun)
        assertFalse(pipe[0].fileDescriptor.valid())
        assertTrue(TunnelService.requestStop()) // idempotent; no second queued teardown
        pipe[1].close();controller.destroy()
    }
    @Test fun cancelled_startup_cannot_attach_a_late_tun() {
        val controller=Robolectric.buildService(TunnelService::class.java).create();val service=controller.get()
        TunnelService.requestStop();val pipe=ParcelFileDescriptor.createPipe()
        try {service.attachTun(pipe[0]);fail()} catch(_:IllegalStateException) {}
        assertFalse(pipe[0].fileDescriptor.valid());pipe[1].close();controller.destroy()
    }
    @Test fun a_previous_connection_health_round_cannot_overwrite_new_status() {
        val controller=Robolectric.buildService(TunnelService::class.java).create();val service=controller.get()
        val epoch=TunnelService::class.java.getDeclaredField("connectionEpoch").apply {isAccessible=true}
        epoch.setLong(service,2L);TunnelStatus.update("ready")
        TunnelService::class.java.getDeclaredMethod("checkHealth",Long::class.javaPrimitiveType,Long::class.javaPrimitiveType).apply {isAccessible=true}.invoke(service,1L,0L)
        assertEquals("ready",TunnelStatus.value.stage)
        controller.destroy()
    }
    @Test fun previous_network_round_and_exit_lookup_cannot_publish_stale_results() {
        val controller=Robolectric.buildService(TunnelService::class.java).create();val service=controller.get()
        fun field(name:String,value:Long) {TunnelService::class.java.getDeclaredField(name).apply {isAccessible=true}.setLong(service,value)}
        field("connectionEpoch",2L);field("healthGeneration",3L);field("networkEpoch",2L)
        TunnelStatus.update("ready");TunnelStatus.exit("203.0.113.2")
        TunnelService::class.java.getDeclaredMethod("checkHealth",Long::class.javaPrimitiveType,Long::class.javaPrimitiveType)
            .apply {isAccessible=true}.invoke(service,2L,2L)
        TunnelService::class.java.getDeclaredMethod("checkExitIp",Long::class.javaPrimitiveType,Long::class.javaPrimitiveType)
            .apply {isAccessible=true}.invoke(service,2L,1L)
        assertEquals("ready",TunnelStatus.value.stage);assertEquals("203.0.113.2",TunnelStatus.value.exitIp)
        TunnelStatus.clearExit();assertNull(TunnelStatus.value.exitIp)
        controller.destroy()
    }
}
