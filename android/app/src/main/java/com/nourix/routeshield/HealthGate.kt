package com.nourix.routeshield
/** A single probe failure must not churn a previously healthy route. */
class HealthGate(private val preferStability:Boolean=true) {
    private var hadSuccess=false
    private var failures=0
    fun success() {hadSuccess=true;failures=0}
    fun failureNeedsRecovery():Boolean {
        failures=(failures+1).coerceAtMost(3)
        return !preferStability || !hadSuccess || failures>=2
    }
}
