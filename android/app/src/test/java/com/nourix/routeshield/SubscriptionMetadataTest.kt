package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
class SubscriptionMetadataTest {
    @Test fun impossible_expiry_and_negative_values_are_ignored() {
        val usage=SubscriptionMetadata.parse("expire=9223372036854775807; upload=-1; total=100")
        assertFalse(usage.has("expire")); assertFalse(usage.has("upload")); assertEquals(100,usage.getLong("total"))
    }
    @Test fun huge_usage_never_overflows_into_fake_remaining_quota() {
        val usage=SubscriptionMetadata.parse("total=100; upload=9223372036854775807; download=9223372036854775807")
        assertEquals(0L,SubscriptionMetadata.remaining(usage))
    }
    @Test fun unknown_quota_is_not_zero() {
        assertNull(SubscriptionMetadata.remaining(SubscriptionMetadata.parse(null)))
        assertEquals(70L,SubscriptionMetadata.remaining(SubscriptionMetadata.parse("total=100; upload=10; download=20; expire=1893456000")))
    }
}
