package com.nourix.routeshield
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
class AppRoutingPolicyTest {
    @Test fun expanded_catalog_covers_verified_apps_without_prefix_guesses() {
        val data=JSONObject().put("autoDomesticApps",true)
        for(pkg in listOf("com.aparat.filimo","com.mydigipay.app.android","taxi.tap30.passenger.play","ir.balad","ir.nasim")) assertFalse(AppRoutingPolicy.enabled(data,pkg))
        assertTrue(AppRoutingPolicy.enabled(data,"ir.balad.fake"));assertTrue(AppRoutingPolicy.enabled(data,"ir.bank.unknown"))
    }
    @Test fun first_launch_routes_every_application_through_vpn() { assertTrue(AppRoutingPolicy.enabled(JSONObject(),"com.example")); assertTrue(AppRoutingPolicy.enabled(JSONObject(),"com.digikala")) }
    @Test fun disabling_one_switch_only_excludes_that_application() { val data=JSONObject(); AppRoutingPolicy.change(data,"com.example",false); assertFalse(AppRoutingPolicy.enabled(data,"com.example")); assertTrue(AppRoutingPolicy.enabled(data,"com.other")) }
    @Test fun automatic_rules_are_explicit_exact_and_offline() { val data=JSONObject().put("autoDomesticApps",true); assertFalse(AppRoutingPolicy.enabled(data,"com.digikala")); assertTrue(AppRoutingPolicy.enabled(data,"com.digikala.fake")); assertTrue(AppRoutingPolicy.enabled(data,"ir.unknown")) }
    @Test fun manual_on_overrides_automatic_exclusion() { val data=JSONObject().put("autoDomesticApps",true); AppRoutingPolicy.change(data,"com.digikala",true); assertTrue(AppRoutingPolicy.enabled(data,"com.digikala")); assertFalse(AppRoutingPolicy.isAutomatic(data,"com.digikala")) }
    @Test fun stale_all_mode_selections_do_not_become_exclusions() { val data=JSONObject().put("mode","all").put("apps",JSONArray().put("com.old")); AppRoutingPolicy.change(data,"com.new",false); assertTrue(AppRoutingPolicy.enabled(data,"com.old")); assertFalse(AppRoutingPolicy.enabled(data,"com.new")) }
    @Test fun native_exclusions_only_include_installed_apps_and_never_self() { val data=JSONObject().put("mode","exclude").put("apps",JSONArray().put("self").put("gone").put("present")); assertEquals(setOf("present"),AppRoutingPolicy.excluded(data,setOf("self","present","other"),"self")) }
}
