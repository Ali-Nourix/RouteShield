package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
class ParallelHealthProbeTest {
    @Test fun healthy_provider_wins_without_waiting_for_dead_provider() {
        val pool=Executors.newFixedThreadPool(2); val started=CountDownLatch(1); val cancelled=CountDownLatch(1)
        try {
            val latency=ParallelHealthProbe.measure(pool,listOf(
                {started.countDown(); try {Thread.sleep(20_000); null} finally {cancelled.countDown()}},
                {assertTrue(started.await(1,TimeUnit.SECONDS)); 27L}),1000)
            assertEquals(27L,latency);assertTrue(cancelled.await(1,TimeUnit.SECONDS))
        } finally {pool.shutdownNow()}
    }
    @Test fun one_exception_does_not_discard_other_provider_success() {
        val pool=Executors.newFixedThreadPool(2)
        try {assertEquals(42L,ParallelHealthProbe.measure(pool,listOf({error("offline")},{42L})))} finally {pool.shutdownNow()}
    }
    @Test fun failed_and_timed_out_rounds_do_not_report_zero_latency() {
        val pool=Executors.newFixedThreadPool(2)
        try {
            assertNull(ParallelHealthProbe.measure(pool,listOf({null},{null})))
            assertNull(ParallelHealthProbe.measure(pool,listOf({Thread.sleep(20_000); 12L}),25))
        } finally {pool.shutdownNow()}
    }
    @Test fun recovery_cadence_is_bounded_and_healthy_cadence_is_quiet() {
        assertEquals(3000L,HealthCadence.delay("degraded"));assertEquals(5000L,HealthCadence.delay("waiting"));assertEquals(20_000L,HealthCadence.delay("online"))
        val gate=HealthGate();gate.success();gate.networkChanged();assertTrue(gate.failureNeedsRecovery())
    }
}
