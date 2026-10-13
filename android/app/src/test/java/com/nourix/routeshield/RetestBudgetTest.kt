package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
class RetestBudgetTest {
    @Test fun first_failure_retests_immediately_then_backs_off() {
        val b = RetestBudget(); assertTrue(b.permit(1000)); assertFalse(b.permit(5999)); assertTrue(b.permit(6000)); assertFalse(b.permit(15999)); assertTrue(b.permit(16000))
    }
    @Test fun successful_traffic_resets_the_outage_budget() {
        val b = RetestBudget(); b.permit(1000); b.healthy(); assertTrue(b.permit(2000))
    }
    @Test fun consecutive_failures_have_a_one_minute_ceiling() {
        val b = RetestBudget(); var time = 0L
        repeat(10) { assertTrue(b.permit(time)); time += 60000 }
        assertTrue(b.permit(time)); assertFalse(b.permit(time+59999)); assertTrue(b.permit(time+60000))
    }
}
