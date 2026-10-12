package com.nourix.routeshield

import org.junit.Assert.*
import org.junit.Test
import org.json.JSONObject
import org.json.JSONArray
import java.nio.file.Files
import java.nio.file.Paths
import java.util.Base64

class ProfileConfigTest {
    private val uuid = "b1f0e4c2-0000-4000-8000-000000009ac4"
    private val samples = mapOf(
        "vless" to "vless://$uuid@relay.example:443?security=tls&type=ws&path=%2Fvpn&sni=relay.example#VLESS",
        "reality" to "vless://$uuid@relay.example:443?security=reality&pbk=_TjUAUe2m107-7gwTN9jNfWz_FEUrEacrL37qj64_D0&sid=ab12#Reality",
        "trojan" to "trojan://test-only@relay.example:443#Trojan",
        "shadowsocks" to "ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ=@relay.example:8388#SS",
        "hysteria2" to "hysteria2://test-only@relay.example:443#HY2",
        "tuic" to "tuic://$uuid:test-only@relay.example:443#TUIC",
        "anytls" to "anytls://test-only@relay.example:443#AnyTLS",
        "vmess" to "vmess://" + Base64.getEncoder().encodeToString("""{"add":"relay.example","port":443,"id":"$uuid","tls":"tls","net":"tcp"}""".toByteArray())
    )
    @Test fun supported_links_generate_native_engine_fixtures() {
        val dir = Paths.get("build/qa-fixtures"); Files.createDirectories(dir)
        samples.forEach { (name, link) ->
            val nodes = ProfileConfig.import(link)
            assertEquals(1, nodes.length())
            val runtime = ProfileConfig.runtime(nodes, 0)
            val root = JSONObject(runtime)
            assertEquals("proxy", root.getJSONObject("route").getString("final"))
            assertEquals(2, root.getJSONArray("inbounds").getJSONObject(0).getJSONArray("address").length())
            assertFalse(root.toString().contains("supabase"))
            Files.write(dir.resolve("$name.json"), runtime.toByteArray())
        }
    }
    @Test fun automatic_group_preserves_sessions_and_tolerance() {
        val nodes = ProfileConfig.import(samples.getValue("trojan") + "\n" + samples.getValue("anytls"))
        val root = JSONObject(ProfileConfig.runtime(nodes, 0)); val group = root.getJSONArray("outbounds").getJSONObject(2)
        assertEquals("urltest", group.getString("type")); assertFalse(group.getBoolean("interrupt_exist_connections")); assertEquals(150, group.getInt("tolerance"))
        Files.createDirectories(Paths.get("build/qa-fixtures")); Files.write(Paths.get("build/qa-fixtures/automatic.json"), root.toString().toByteArray())
        assertEquals(2, JSONObject(ProfileConfig.runtime(nodes, 1)).getJSONArray("outbounds").length())
    }
    @Test fun duplicates_do_not_create_duplicate_nodes() { val link = samples.getValue("trojan"); assertEquals(1, ProfileConfig.import("$link\n$link").length()) }
    @Test fun base64_subscriptions_are_imported() {
        val encoded = Base64.getEncoder().encodeToString(samples.getValue("trojan").toByteArray())
        assertEquals("trojan", ProfileConfig.import(encoded).getJSONObject(0).getString("type"))
    }
    @Test fun imported_json_cannot_create_arbitrary_inbounds_or_detours() {
        val input = """{"inbounds":[{"type":"mixed","listen":"0.0.0.0"}],"outbounds":[{"type":"http","server":"relay.example","server_port":443,"detour":"evil"}]}"""
        val root = JSONObject(ProfileConfig.runtime(ProfileConfig.import(input), 0))
        assertEquals("tun", root.getJSONArray("inbounds").getJSONObject(0).getString("type"))
        assertFalse(root.getJSONArray("outbounds").getJSONObject(0).has("detour"))
    }
    @Test fun insecure_tls_is_rejected() {
        try { ProfileConfig.import("trojan://password@relay.example:443?allowInsecure=1"); fail() } catch (_: IllegalArgumentException) {}
        try { ProfileConfig.import("""{"type":"trojan","server":"relay.example","server_port":443,"tls":{"enabled":true,"insecure":true}}"""); fail() } catch (_: IllegalArgumentException) {}
    }
    @Test fun invalid_selection_is_not_a_direct_route() {
        try { ProfileConfig.runtime(ProfileConfig.import(samples.getValue("trojan")), 99); fail() } catch (_: IllegalArgumentException) {}
    }
    @Test fun oversized_and_empty_inputs_are_rejected() {
        for (text in listOf("", "x".repeat(ProfileConfig.MAX_BYTES+1))) { try { ProfileConfig.import(text); fail() } catch (_: IllegalArgumentException) {} }
    }
    @Test fun uri_plus_is_not_changed_to_a_space() { assertEquals("a+b", ProfileConfig.decode("a+b")) }

    @Test fun wireguard_is_a_userspace_endpoint() {
        val key=Base64.getEncoder().encodeToString(ByteArray(32) { 1 })
        val input="[Interface]\nAddress = 10.8.0.2/32\nPrivateKey = $key\n[Peer]\nPublicKey = $key\nEndpoint = relay.example:51820\nAllowedIPs = 0.0.0.0/0, ::/0\nPersistentKeepalive = 25"
        val root=JSONObject(ProfileConfig.runtime(ProfileConfig.import(input),0))
        assertEquals("wireguard",root.getJSONArray("endpoints").getJSONObject(0).getString("type"))
        assertFalse(root.getJSONArray("endpoints").getJSONObject(0).getBoolean("system"))
        Files.createDirectories(Paths.get("build/qa-fixtures")); Files.write(Paths.get("build/qa-fixtures/wireguard.json"),root.toString().toByteArray())
        try { ProfileConfig.import(input.replace("[Peer]","Jc = 3\n[Peer]")); fail() } catch (_: IllegalArgumentException) {}
    }
    @Test fun explicit_settings_are_native_rules_not_security_downgrades() {
        val settings=JSONObject().put("fragmentTls",true).put("domestic",true).put("secureDns",true)
        val root=JSONObject(ProfileConfig.runtime(ProfileConfig.import(samples.getValue("trojan")),0,settings))
        assertTrue(root.getJSONArray("outbounds").getJSONObject(0).getJSONObject("tls").getBoolean("fragment"))
        assertEquals("dns-remote",root.getJSONObject("dns").getString("final"))
        assertEquals("proxy",root.getJSONObject("route").getString("final"))
        Files.write(Paths.get("build/qa-fixtures/settings.json"),root.toString().toByteArray())
    }
    @Test fun encrypted_library_selection_uses_the_selected_group() {
        val a=ProfileConfig.import(samples.getValue("trojan")); val b=ProfileConfig.import(samples.getValue("anytls"))
        val saved=JSONObject().put("active",1).put("library",JSONArray().put(JSONObject().put("nodes",a)).put(JSONObject().put("nodes",b)))
        assertEquals("anytls",ProfileConfig.activeNodes(saved).getJSONObject(0).getString("type"))
    }
}
