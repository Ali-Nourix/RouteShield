package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.Executors
class InternetVerificationTest {
    @Test fun one_cdn_is_not_full_internet_access() {
        val pool=Executors.newFixedThreadPool(3)
        try {
            val result=ParallelHealthProbe.verify(pool,listOf({23L},{null},{Long.MAX_VALUE}))
            assertFalse(result.verified);assertTrue(result.partial);assertEquals(2,result.successes)
        } finally {pool.shutdownNow()}
    }
    @Test fun dns_failure_prevents_verified_state_even_if_https_is_cached() {
        val pool=Executors.newFixedThreadPool(3)
        try {assertFalse(ParallelHealthProbe.verify(pool,listOf({10L},{20L},{null})).verified)} finally {pool.shutdownNow()}
    }
    @Test fun both_https_and_dns_must_pass_and_dns_is_not_the_rtt() {
        val pool=Executors.newFixedThreadPool(3)
        try {
            val result=ParallelHealthProbe.verify(pool,listOf({34L},{50L},{Long.MAX_VALUE}))
            assertTrue(result.verified);assertFalse(result.partial);assertEquals(34L,result.latencyMs)
        } finally {pool.shutdownNow()}
    }
    @Test fun timeout_keeps_partial_state_and_cancels_other_checks() {
        val pool=Executors.newFixedThreadPool(3)
        try {
            val result=ParallelHealthProbe.verify(pool,listOf({3L},{Thread.sleep(10_000);12L},{Long.MAX_VALUE}),200)
            assertFalse(result.verified);assertTrue(result.partial)
        } finally {pool.shutdownNow()}
    }
    @Test fun new_profile_clears_old_connection_evidence() {
        TunnelStatus.update("idle");TunnelStatus.update("online",20);TunnelStatus.exit("203.0.113.1")
        TunnelStatus.group("node-1",mapOf("node-1" to 20));TunnelStatus.checks(2,true)
        TunnelStatus.update("starting")
        assertNull(TunnelStatus.value.exitIp);assertTrue(TunnelStatus.value.delays.isEmpty())
        assertNull(TunnelStatus.value.selected);assertNull(TunnelStatus.value.dnsVerified);assertTrue(TunnelStatus.value.latencyHistory.isEmpty())
        TunnelStatus.update("idle")
    }
}
