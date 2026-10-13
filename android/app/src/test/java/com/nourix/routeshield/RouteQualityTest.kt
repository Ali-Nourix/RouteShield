package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
class RouteQualityTest {
    @Test fun isolated_latency_spike_does_not_trigger_retest() {
        val q=RouteQuality();assertFalse(q.needsRemeasure(70));assertFalse(q.needsRemeasure(600));assertFalse(q.needsRemeasure(80));assertFalse(q.needsRemeasure(700))
    }
    @Test fun sustained_regression_requests_remeasurement_not_disconnect() {
        val q=RouteQuality();q.needsRemeasure(100);assertFalse(q.needsRemeasure(600));assertFalse(q.needsRemeasure(650));assertTrue(q.needsRemeasure(600))
    }
    @Test fun a_new_network_or_server_learns_its_own_baseline() {
        val q=RouteQuality();q.needsRemeasure(40);q.needsRemeasure(600);q.reset();repeat(5){assertFalse(q.needsRemeasure(600))}
    }
}
