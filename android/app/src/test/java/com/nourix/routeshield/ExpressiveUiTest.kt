package com.nourix.routeshield

import androidx.activity.compose.setContent
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.assertIsEnabled
import com.github.takahirom.roborazzi.captureRoboImage
import org.junit.Before
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
    @Before fun resetTunnel() {TunnelStatus.update("idle")}
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    @Test fun english_tabs_render() {
        File("build/ui-qa").mkdirs()
        compose.waitForIdle()
        compose.activity.window.decorView.captureRoboImage("build/ui-qa/connection-light.png")
        compose.onNodeWithText("Configure system Kill Switch").performScrollTo();compose.waitForIdle()
        compose.onNodeWithText("Configure system Kill Switch").assertExists()
        compose.activity.window.decorView.captureRoboImage("build/ui-qa/kill-switch-light.png")
        for(label in listOf("Profiles","Applications","Settings")) {
            compose.onNodeWithText(label).performClick(); compose.waitForIdle()
            compose.activity.window.decorView.captureRoboImage("build/ui-qa/${label.lowercase()}-light.png")
            if(label=="Settings") {
                compose.onNodeWithText("Prefer stable automatic routes").performScrollTo();compose.waitForIdle()
                compose.activity.window.decorView.captureRoboImage("build/ui-qa/stability-settings-light.png")
            }
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

    @Test fun disconnect_requests_framework_stop_and_recovers_stale_state() {
        compose.waitForIdle(); compose.runOnUiThread {TunnelStatus.update("online",50)};compose.waitForIdle()
        compose.onNodeWithText("Disconnect").performClick();compose.waitForIdle()
        org.junit.Assert.assertEquals("idle",TunnelStatus.value.stage)
    }
    @Test fun populated_profile_layout_and_blue_theme_render() {
        val model=androidx.lifecycle.ViewModelProvider(compose.activity)[ShieldModel::class.java]
        compose.waitForIdle(); compose.runOnUiThread {
            val nodes=ProfileConfig.import("trojan://test-only@relay.example:443#Frankfurt\nanytls://test-only@relay.example:443#Amsterdam\nvless://b1f0e4c2-0000-4000-8000-000000009ac4@relay.example:443?security=tls#Backup")
            model.saved.put("theme","light").put("themeSeed","blue").put("language","en").put("library",org.json.JSONArray()
                .put(org.json.JSONObject().put("name","Everyday routes").put("nodes",nodes))
                .put(org.json.JSONObject().put("name","Backup routes").put("nodes",nodes)))
            compose.activity.setContent {ShieldApp(model,{}, {}, {})}
        }
        compose.waitForIdle();compose.onNodeWithText("Profiles").performClick();compose.waitForIdle()
        File("build/ui-qa").mkdirs();compose.activity.window.decorView.captureRoboImage("build/ui-qa/profiles-populated-blue.png")
        compose.runOnUiThread {TunnelStatus.update("online",50)};compose.waitForIdle()
        compose.onNodeWithText("Frankfurt").assertIsEnabled()
        compose.onNodeWithText("Automatic").assertIsEnabled()
    }
    @Test fun theme_color_choices_are_visible() {
        compose.waitForIdle();compose.onNodeWithText("Settings").performClick();compose.waitForIdle()
        compose.onNodeWithText("Theme color").assertExists();compose.onNodeWithText("Blue").assertExists()
    }
    @Test fun route_picker_exposes_manual_and_automatic_choices() {
        val model=androidx.lifecycle.ViewModelProvider(compose.activity)[ShieldModel::class.java]
        compose.waitForIdle();compose.runOnUiThread {
            val nodes=ProfileConfig.import("trojan://test-only@relay.example:443#Frankfurt\nanytls://test-only@relay.example:443#Amsterdam")
            model.saved.put("language","en").put("library",org.json.JSONArray().put(org.json.JSONObject().put("name","Everyday routes").put("nodes",nodes)))
            compose.activity.setContent {ShieldApp(model,{}, {}, {})}
        }
        compose.waitForIdle()
        compose.onNodeWithText("Current server").performScrollTo()
        compose.onNodeWithContentDescription("Change server").performClick();compose.waitForIdle()
        compose.onNodeWithText("Amsterdam").assertIsEnabled()
        compose.onAllNodesWithText("Automatic")[1].assertIsEnabled()
        // AndroidKeyStore + JNI selection persistence require a phone, not this host screenshot test.
        File("build/ui-qa").mkdirs()
        compose.onNodeWithTag("route-sheet").captureRoboImage("build/ui-qa/connection-server-picker.png")
    }
    @Test fun persian_dark_large_text_screens_render() {
        val model=androidx.lifecycle.ViewModelProvider(compose.activity)[ShieldModel::class.java]
        compose.waitForIdle();compose.runOnUiThread {
            model.saved.put("theme","dark").put("language","fa").put("library",org.json.JSONArray().put(org.json.JSONObject()
                .put("name","مسیرهای روزانه").put("nodes",ProfileConfig.import("trojan://test-only@relay.example:443#Test"))))
            compose.activity.setContent {
                val density=androidx.compose.ui.platform.LocalDensity.current
                val config=android.content.res.Configuration(androidx.compose.ui.platform.LocalConfiguration.current).apply {fontScale=2f}
                androidx.compose.runtime.CompositionLocalProvider(
                    androidx.compose.ui.platform.LocalDensity provides androidx.compose.ui.unit.Density(density.density,2f),
                    androidx.compose.ui.platform.LocalConfiguration provides config) {ShieldApp(model,{}, {}, {})}
            }
        }
        compose.waitForIdle();File("build/ui-qa").mkdirs()
        compose.activity.window.decorView.captureRoboImage("build/ui-qa/connection-dark-fa-font200.png")
        for(label in listOf("پروفایل‌ها","برنامه‌ها","تنظیمات")) {
            compose.onNodeWithText(label).performClick();compose.waitForIdle()
            compose.activity.window.decorView.captureRoboImage("build/ui-qa/font200-fa-${label.hashCode()}.png")
        }
    }
    @Test fun connected_expressive_layout_with_fixture_measurements() {
        val model=androidx.lifecycle.ViewModelProvider(compose.activity)[ShieldModel::class.java]
        compose.waitForIdle();compose.runOnUiThread {
            model.saved.put("themeSeed","teal").put("language","en").put("library",org.json.JSONArray().put(org.json.JSONObject()
                .put("name","Everyday routes").put("nodes",ProfileConfig.import("trojan://test-only@relay.example:443#Amsterdam"))))
            // UI fixture values, not a real VPN benchmark or traffic observation.
            listOf(90L,70L,110L,80L,65L,85L).forEach {TunnelStatus.update("online",it)}
            TunnelStatus.metrics(10240,540672,50000,2300000)
            compose.activity.setContent {ShieldApp(model,{}, {}, {})}
        }
        compose.waitForIdle();File("build/ui-qa").mkdirs()
        compose.activity.window.decorView.captureRoboImage("build/ui-qa/expressive-online-fixture-teal.png")
        compose.onNodeWithText("Disconnect").assertIsEnabled()
        compose.runOnUiThread {
            model.saved.put("theme","dark").put("language","fa")
            compose.activity.setContent {ShieldApp(model,{}, {}, {})}
        }
        compose.waitForIdle()
        compose.activity.window.decorView.captureRoboImage("build/ui-qa/expressive-online-dark-fa.png")
        compose.onNodeWithText("قطع اتصال").assertIsEnabled()
    }
    @Test fun file_import_uses_system_picker_without_storage_permission() {
        compose.waitForIdle();compose.onNodeWithText("Profiles").performClick();compose.waitForIdle()
        compose.onNodeWithText("Import .conf or profile file").performClick();compose.waitForIdle()
        val started=org.robolectric.Shadows.shadowOf(compose.activity).nextStartedActivityForResult
        org.junit.Assert.assertNotNull(started)
        org.junit.Assert.assertEquals(android.content.Intent.ACTION_OPEN_DOCUMENT,started.intent.action)
        org.junit.Assert.assertEquals("*/*",started.intent.type)
    }
    @Test fun invalid_paste_keeps_input_and_explains_error_in_dialog() {
        compose.waitForIdle();compose.onNodeWithText("Profiles").performClick();compose.waitForIdle()
        // Freeze the blinking editable cursor / sheet motion while driving this host test.
        // Explicit frames still apply edits and all final UI/error assertions remain active.
        compose.mainClock.autoAdvance=false
        compose.onNodeWithText("Import profiles").performClick()
        compose.mainClock.advanceTimeBy(1000);compose.waitForIdle()
        val field=compose.onNodeWithText("Link or config")
        field.performTextInput("not-a-config")
        compose.mainClock.advanceTimeBy(300);compose.waitForIdle()
        compose.onNodeWithText("Import",useUnmergedTree=true).performClick()
        val model=androidx.lifecycle.ViewModelProvider(compose.activity)[ShieldModel::class.java]
        compose.waitUntil(5_000) {
            compose.mainClock.advanceTimeByFrame() // Also dispatch ViewModel completion on the paused host clock.
            !model.busy
        }
        compose.mainClock.advanceTimeBy(300);compose.waitForIdle()
        compose.onNodeWithText("Could not import. Check the config, keys, or HTTPS subscription.").assertExists()
        field.assertTextContains("not-a-config")
        File("build/ui-qa").mkdirs()
        compose.onNodeWithTag("profile-import-sheet").captureRoboImage("build/ui-qa/import-error-retains-input.png")
        compose.onNodeWithText("Cancel").performClick()
        compose.mainClock.advanceTimeBy(1000);compose.mainClock.autoAdvance=true
    }
}
