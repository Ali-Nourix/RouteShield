package com.nourix.routeshield

/** Monotonic backoff keeps outage recovery responsive without continuously probing every node. */
class RetestBudget {
    private var next = 0L
    private var gap = 5_000L
    fun healthy() { next = 0L; gap = 5_000L }
    fun permit(now: Long): Boolean {
        if (now < next) return false
        next = now + gap; gap = (gap * 2).coerceAtMost(60_000L)
        return true
    }
}
