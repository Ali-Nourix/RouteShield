package com.nourix.routeshield

/** Sort indices, never the stored nodes: selector tags must retain their original identity. */
object NodeOrder {
    fun byLatency(size:Int,delays:Map<String,Int>):List<Int> = (0 until size).sortedWith(
        compareBy<Int> {delays["node-$it"]?.takeIf {ms->ms>0} ?: Int.MAX_VALUE}.thenBy {it})
}
