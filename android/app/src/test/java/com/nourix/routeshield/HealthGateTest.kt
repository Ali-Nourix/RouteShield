package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
class HealthGateTest {
    @Test fun transient_failure_does_not_churn_a_healthy_route() {
        val gate=HealthGate();gate.success();assertFalse(gate.failureNeedsRecovery());assertTrue(gate.failureNeedsRecovery())
    }
    @Test fun success_resets_consecutive_failure_count() {
        val gate=HealthGate();gate.success();gate.failureNeedsRecovery();gate.success();assertFalse(gate.failureNeedsRecovery())
    }
    @Test fun failed_startup_and_speed_priority_recover_without_hysteresis_delay() {
        assertTrue(HealthGate().failureNeedsRecovery());val gate=HealthGate(false);gate.success();assertTrue(gate.failureNeedsRecovery())
    }
}
