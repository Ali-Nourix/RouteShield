package com.nourix.routeshield

import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException

/** Only one task of each kind may be queued/running; slow metadata must not block health. */
class BackgroundWork:AutoCloseable {
    private val executor=Executors.newFixedThreadPool(2)
    private val pending=ConcurrentHashMap.newKeySet<String>()
    fun submit(kind:String,action:()->Unit):Boolean {
        if(!pending.add(kind)) return false
        return try {
            executor.execute {try {action()} finally {pending.remove(kind)}};true
        } catch(_:RejectedExecutionException) {pending.remove(kind);false}
    }
    override fun close() {executor.shutdownNow()}
}
