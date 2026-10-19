package com.nourix.routeshield

import androidx.compose.ui.graphics.luminance
import com.nourix.routeshield.designsystem.ConnectionFeedback
import org.junit.Assert.assertTrue
import org.junit.Test

class ConnectionFeedbackTest {
    @Test fun success_icon_has_accessible_contrast_in_both_modes() {
        listOf(false,true).forEach {dark->
            val a=ConnectionFeedback.fill(dark).luminance();val b=ConnectionFeedback.content(dark).luminance()
            assertTrue((maxOf(a,b)+0.05f)/(minOf(a,b)+0.05f)>=4.5f)
        }
    }
}
