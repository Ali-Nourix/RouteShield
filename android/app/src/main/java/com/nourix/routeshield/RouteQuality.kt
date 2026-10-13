package com.nourix.routeshield

/** Detect sustained RTT regression, not throughput. Never disconnect or select a manual route. */
class RouteQuality {
    private var baseline: Double? = null
    private var slowRounds = 0
    fun reset() { baseline=null;slowRounds=0 }
    fun needsRemeasure(latencyMs: Long): Boolean {
        require(latencyMs>=0)
        val normal=baseline
        if(normal==null) {baseline=latencyMs.toDouble();return false}
        if(latencyMs>maxOf(normal*2.0,normal+250.0)) {
            slowRounds++
            if(slowRounds>=3) {slowRounds=0;return true}
        } else {slowRounds=0;baseline=normal*0.8+latencyMs*0.2}
        return false
    }
}
