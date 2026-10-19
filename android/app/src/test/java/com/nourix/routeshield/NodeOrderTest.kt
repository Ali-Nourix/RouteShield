package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
class NodeOrderTest {
    @Test fun lowest_valid_ping_first_unknown_and_failed_last() {
        assertEquals(listOf(2,0,3,1,4,5),NodeOrder.byLatency(6,mapOf("node-0" to 70,"node-1" to 0,"node-2" to 20,"node-3" to 70,"node-4" to -1)))
    }
    @Test fun sorting_retains_selector_identity_in_large_profiles() {
        val order=NodeOrder.byLatency(500,mapOf("node-499" to 1,"node-44" to 2))
        assertEquals(listOf(499,44),order.take(2));assertEquals((0 until 500).toSet(),order.toSet())
        assertEquals("node-499",ProfileConfig.selectionTag(500,order.first()+1))
    }
}
