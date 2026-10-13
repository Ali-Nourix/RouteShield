package com.nourix.routeshield

import java.util.concurrent.Callable
import java.util.concurrent.ExecutorCompletionService
import java.util.concurrent.ExecutorService
import java.util.concurrent.TimeUnit

/** First verified HTTPS 204 wins. A dead provider cannot delay a healthy provider.
 * Values measure only the successful request, not time spent waiting on a failed provider.
 * Caller must disconnect I/O on return: cancelling a Future alone does not close sockets.
 */
object ParallelHealthProbe {
    fun measure(executor: ExecutorService, checks: List<()->Long?>, deadlineMs: Long = 3500): Long? {
        require(checks.size in 1..2 && deadlineMs in 1..10_000)
        val completed = ExecutorCompletionService<Long?>(executor)
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(deadlineMs)
        val futures = checks.map { check -> completed.submit(Callable { runCatching(check).getOrNull() }) }
        try {
            repeat(checks.size) {
                val remaining = deadline - System.nanoTime()
                if (remaining <= 0) return null
                val result = completed.poll(remaining, TimeUnit.NANOSECONDS) ?: return null
                val latency = result.get()
                if (latency != null && latency >= 0) return latency
            }
            return null
        } finally { futures.forEach { it.cancel(true) } }
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
