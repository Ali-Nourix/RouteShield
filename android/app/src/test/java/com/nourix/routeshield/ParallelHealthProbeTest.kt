package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
class ParallelHealthProbeTest {
    @Test fun incomplete_provider_cannot_mark_full_online_and_is_cancelled() {
        val pool=Executors.newFixedThreadPool(2);val started=CountDownLatch(1);val cancelled=CountDownLatch(1)
        try {
            val result=ParallelHealthProbe.verify(pool,listOf(
                {started.countDown();try {Thread.sleep(20_000);null} finally {cancelled.countDown()}},
                {assertTrue(started.await(1,TimeUnit.SECONDS));27L}),200)
            assertFalse(result.verified);assertTrue(result.partial);assertEquals(27L,result.latencyMs)
            assertTrue(cancelled.await(1,TimeUnit.SECONDS))
        } finally {pool.shutdownNow()}
    }
    @Test fun exception_retains_partial_evidence_but_not_online() {
        val pool=Executors.newFixedThreadPool(2)
        try {val result=ParallelHealthProbe.verify(pool,listOf({error("offline")},{42L}));assertTrue(result.partial);assertFalse(result.verified)} finally {pool.shutdownNow()}
    }
    @Test fun completely_failed_round_does_not_report_zero_latency() {
        val pool=Executors.newFixedThreadPool(2)
        try {val result=ParallelHealthProbe.verify(pool,listOf({null},{null}));assertNull(result.latencyMs);assertFalse(result.verified);assertFalse(result.partial)} finally {pool.shutdownNow()}
    }
    @Test fun partial_connectivity_is_rechecked_promptly() {
        assertEquals(3000L,HealthCadence.delay("degraded"));assertEquals(5000L,HealthCadence.delay("waiting"));assertEquals(20_000L,HealthCadence.delay("online"))
        val gate=HealthGate();gate.success();gate.networkChanged();assertTrue(gate.failureNeedsRecovery())
    }
}
