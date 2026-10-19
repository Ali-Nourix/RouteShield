package com.nourix.routeshield
import android.app.Application
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
@RunWith(RobolectricTestRunner::class)
@Config(sdk=[35])
class VpnHealthNetworkTest {
    @Test fun physical_default_network_is_not_vpn_evidence() {
        val app=ApplicationProvider.getApplicationContext<Application>()
        assertNull(VpnHealthNetwork.find(app))
    }
}
