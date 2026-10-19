package com.nourix.routeshield

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class BackgroundWorkTest {
    @Test fun slow_metadata_does_not_block_remeasure_and_duplicates_are_coalesced() {
        val work=BackgroundWork();val started=CountDownLatch(1);val release=CountDownLatch(1);val remeasure=CountDownLatch(1)
        try {
            assertTrue(work.submit("exit-ip") {started.countDown();release.await(3,TimeUnit.SECONDS)})
            assertTrue(started.await(1,TimeUnit.SECONDS))
            assertFalse(work.submit("exit-ip") {fail("Duplicate metadata task")})
            assertTrue(work.submit("retest") {remeasure.countDown()})
            assertTrue(remeasure.await(1,TimeUnit.SECONDS))
        } finally {release.countDown();work.close()}
    }
    @Test fun shutdown_rejects_future_work() {
        val work=BackgroundWork();work.close()
        assertFalse(work.submit("exit-ip") {fail("Work after shutdown")})
    }
}
