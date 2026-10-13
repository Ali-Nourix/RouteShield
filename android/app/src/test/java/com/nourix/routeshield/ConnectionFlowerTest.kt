@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)
package com.nourix.routeshield

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.toPixelMap
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.captureToImage
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import com.github.takahirom.roborazzi.captureRoboImage
import com.nourix.routeshield.designsystem.ExpressiveConnectControl
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import java.io.File

/** Host-rendered control regression, not a live VPN/device test. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk=[35],qualifiers="w393dp-h851dp-xxhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class ConnectionFlowerTest {
    @get:Rule val compose=createComposeRule()

    @Test fun connection_changes_color_but_preserves_the_disconnected_flower() {
        val running=mutableStateOf(false)
        compose.setContent {
            MaterialExpressiveTheme(colorScheme=lightColorScheme(
                primary=Color.Red,onPrimary=Color.White,
                tertiaryContainer=Color.Blue,onTertiaryContainer=Color.White)) {
                Box(Modifier.background(Color.Black).testTag("control")) {
                    // Keep the label identical so the comparison isolates state styling.
                    ExpressiveConnectControl("Connect","fixture",running.value,false,true,{})
                }
            }
        }
        val control=compose.onNodeWithTag("control")
        compose.waitForIdle()
        val disconnected=control.captureToImage()
        File("build/ui-qa").mkdirs()
        control.captureRoboImage("build/ui-qa/connection-flower-disconnected.png")
        compose.runOnIdle {running.value=true}
        compose.waitForIdle()
        val connected=control.captureToImage()
        control.captureRoboImage("build/ui-qa/connection-flower-connected.png")
        assertSameSilhouette(disconnected,connected)
        assertTrue("Disconnected fill must be primary",coloredPixels(disconnected,true)>100)
        assertTrue("Connected fill must be tertiary",coloredPixels(connected,false)>100)
        compose.runOnIdle {running.value=false}
        compose.waitForIdle()
        assertSameSilhouette(disconnected,control.captureToImage())
    }

    private fun assertSameSilhouette(expected:ImageBitmap,actual:ImageBitmap) {
        assertEquals(expected.width,actual.width)
        assertEquals(expected.height,actual.height)
        val before=expected.toPixelMap();val after=actual.toPixelMap()
        for(y in 0 until expected.height) for(x in 0 until expected.width) {
            // Red and blue use identical coverage thresholds; white icon/text is unchanged.
            fun visible(color:Color)=maxOf(color.red,color.green,color.blue)>0.05f
            assertEquals("Flower coverage changed at ($x,$y)",visible(before[x,y]),visible(after[x,y]))
        }
    }

    private fun coloredPixels(image:ImageBitmap,red:Boolean):Int {
        val pixels=image.toPixelMap();var count=0
        for(y in 0 until image.height) for(x in 0 until image.width) {
            val color=pixels[x,y]
            if(if(red) color.red>0.8f && color.blue<0.1f else color.blue>0.8f && color.red<0.1f) count++
        }
        return count
    }
}
