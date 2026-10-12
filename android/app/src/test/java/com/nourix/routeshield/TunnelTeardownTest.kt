package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
class TunnelTeardownTest {
    @Test fun late_health_result_cannot_undo_stopping() {
        TunnelStatus.update("idle");TunnelStatus.update("stopping");TunnelStatus.update("online",50)
        assertEquals("stopping",TunnelStatus.value.stage);TunnelStatus.update("idle")
    }
    @Test fun packet_reader_is_unblocked_before_native_shutdown() { val steps=mutableListOf<String>(); assertTrue(TunnelTeardown.close({steps.add("tun")},{steps.add("platform")},{assertEquals(listOf("tun","platform"),steps);steps.add("core")},{steps.add("server")},{steps.add("telemetry")}));assertEquals(listOf("tun","platform","core","server","telemetry"),steps) }
    @Test fun failing_cleanup_does_not_skip_later_resources() {var closed=false;assertFalse(TunnelTeardown.close({error("test")},{},{},{},{closed=true}));assertTrue(closed)}
}
