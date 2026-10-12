package com.nourix.routeshield

import androidx.activity.compose.setContent
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import com.github.takahirom.roborazzi.captureRoboImage
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import java.io.File

/** Actual Compose UI captured on Robolectric, NOT a device/VPN traffic test. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk=[35],qualifiers="w393dp-h851dp-xxhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class ExpressiveUiTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    @Test fun english_tabs_render() {
        File("build/ui-qa").mkdirs()
        compose.waitForIdle()
        compose.activity.window.decorView.captureRoboImage("build/ui-qa/connection-light.png")
        for(label in listOf("Profiles","Applications","Settings")) {
            compose.onNodeWithText(label).performClick(); compose.waitForIdle()
            compose.activity.window.decorView.captureRoboImage("build/ui-qa/${label.lowercase()}-light.png")
        }
    }

    @Test fun persian_dark_profiles_render() {
        val model=androidx.lifecycle.ViewModelProvider(compose.activity)[ShieldModel::class.java]
        compose.waitForIdle()
        compose.runOnUiThread {
            model.saved.put("theme","dark").put("language","fa").put("library",org.json.JSONArray().put(org.json.JSONObject()
                .put("name","سرور آزمایشی — مسیر امن").put("nodes",ProfileConfig.import("trojan://test-only@relay.example:443#Test"))))
            compose.activity.setContent { ShieldApp(model, {}, {}, {}) }
        }
        compose.waitForIdle(); File("build/ui-qa").mkdirs()
        compose.activity.window.decorView.captureRoboImage("build/ui-qa/connection-dark-fa.png")
        for(label in listOf("پروفایل‌ها","برنامه‌ها","تنظیمات")) {
            compose.onNodeWithText(label).performClick(); compose.waitForIdle()
            compose.activity.window.decorView.captureRoboImage("build/ui-qa/dark-fa-${label.hashCode()}.png")
        }
    }
    @Test @Config(sdk=[35],qualifiers="w1000dp-h800dp-mdpi") fun tablet_rail_renders() {
        compose.waitForIdle(); File("build/ui-qa").mkdirs()
        compose.activity.window.decorView.captureRoboImage("build/ui-qa/tablet-rail.png")
    }
}
