package com.nourix.routeshield

import java.util.concurrent.Callable
import java.util.concurrent.ExecutorCompletionService
import java.util.concurrent.ExecutorService
import java.util.concurrent.TimeUnit

/** Independent HTTPS + DNS evidence. Caller must close I/O: cancelling futures alone cannot close sockets. */
data class InternetHealth(val successes:Int,val expected:Int,val latencyMs:Long?) {
    val verified get()=successes==expected
    val partial get()=successes in 1 until expected
}
object ParallelHealthProbe {
    /** All independent checks must succeed; one reachable CDN is not general Internet access. */
    fun verify(executor:ExecutorService,checks:List<()->Long?>,deadlineMs:Long=3500):InternetHealth {
        require(checks.size in 2..3 && deadlineMs in 1..10_000)
        val completed=ExecutorCompletionService<Long?>(executor)
        val deadline=System.nanoTime()+TimeUnit.MILLISECONDS.toNanos(deadlineMs)
        val futures=checks.map {check->completed.submit(Callable {runCatching(check).getOrNull()})}
        val latencies=mutableListOf<Long>()
        try {
            repeat(checks.size) {
                val remaining=deadline-System.nanoTime()
                if(remaining<=0) return InternetHealth(latencies.size,checks.size,latencies.minOrNull())
                val result=completed.poll(remaining,TimeUnit.NANOSECONDS)
                    ?: return InternetHealth(latencies.size,checks.size,latencies.minOrNull())
                result.get()?.takeIf {it>=0}?.let(latencies::add)
            }
            return InternetHealth(latencies.size,checks.size,latencies.minOrNull())
        } finally {futures.forEach {it.cancel(true)}}
    }


}

/** Healthy polling is modest; two failed rounds recover within seconds instead of minutes. */
object HealthCadence {
    fun delay(stage: String): Long = when(stage) {
        "degraded" -> 3000L
        "waiting", "ready", "starting" -> 5000L
        else -> 20_000L
    }
}
