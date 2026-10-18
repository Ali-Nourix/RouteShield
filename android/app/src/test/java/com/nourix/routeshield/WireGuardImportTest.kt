package com.nourix.routeshield

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.util.Base64

class WireGuardImportTest {
    private val key=Base64.getEncoder().encodeToString(ByteArray(32) {1}) // Public test fixture only.
    private val config="""# exported profile
[Interface]
PrivateKey = $key
Address = 10.8.0.2/32
Address = fd00::2/128
DNS = 10.8.0.1
MTU = 1380
[Peer]
PublicKey = $key
PresharedKey = $key
Endpoint = [2001:db8::1]:51820
AllowedIPs = 0.0.0.0/0
AllowedIPs = ::/0
PersistentKeepalive = 25
"""
    @Test fun commented_bom_dual_stack_conf_preserves_peer_options() {
        val nodes=ProfileConfig.import("\uFEFF$config")
        val node=nodes.getJSONObject(0);val peer=node.getJSONArray("peers").getJSONObject(0)
        assertEquals(2,node.getJSONArray("address").length());assertEquals(1380,node.getInt("mtu"))
        assertEquals("2001:db8::1",peer.getString("address"));assertEquals(51820,peer.getInt("port"))
        assertEquals(2,peer.getJSONArray("allowed_ips").length());assertEquals(25,peer.getInt("persistent_keepalive_interval"))
        assertEquals(key,peer.getString("pre_shared_key"));assertFalse(node.getBoolean("system"))
        val root=JSONObject(ProfileConfig.runtime(nodes,0))
        assertEquals("dns-remote",root.getJSONObject("dns").getString("final"))
        assertEquals("proxy",root.getJSONObject("route").getString("final"))
        assertEquals(1,root.getJSONArray("endpoints").length())
        assertFalse(root.toString().contains("10.8.0.1")) // File DNS cannot bypass app policy.
    }
    @Test fun encoded_configs_and_multiple_peers_are_supported() {
        assertEquals(1,ProfileConfig.import(Base64.getEncoder().encodeToString(config.toByteArray())).length())
        val second="\n[Peer]\nPublicKey=$key\nEndpoint=relay.example:51820\nAllowedIPs=192.168.0.0/16"
        assertEquals(2,ProfileConfig.import(config+second).getJSONObject(0).getJSONArray("peers").length())
    }
    @Test fun malformed_and_unsafe_settings_fail_closed_without_echoing_keys() {
        val invalid=listOf(
            config.replace("PrivateKey = $key","PrivateKey = invalid-secret"),
            config.replace("Address = 10.8.0.2/32","Address = relay.example/32"),
            config.replace("10.8.0.2/32","10.8.0.2/99"),
            config.replace("fd00::2/128","fd00::2/129"),
            config.replace("51820","99999"),config.replace("MTU = 1380","MTU = wrong"),
            config.replace("PersistentKeepalive = 25","PersistentKeepalive = -1"),
            config.replace("[Peer]","PostUp = curl invalid.example\n[Peer]"),
            config.replace("[Peer]","Jc = 3\n[Peer]"),
            config.replace("[Peer]","[Unknown]"),
            config.replace("AllowedIPs = 0.0.0.0/0\nAllowedIPs = ::/0", ""),
            config.replace("[Peer]","[Interface]\n[Peer]"),
            config.replace("[Peer]","PrivateKey = $key\n[Peer]")
        )
        invalid.forEach {input->
            val error=runCatching {ProfileConfig.import(input)}.exceptionOrNull()
            assertNotNull("Unsafe config accepted",error)
            assertFalse(error!!.message.orEmpty().contains(key))
            assertFalse(error.message.orEmpty().contains("invalid-secret"))
        }
    }
    @Test fun json_wireguard_keys_and_addresses_use_the_same_validation() {
        val node=ProfileConfig.import(config).getJSONObject(0)
        node.put("private_key","bad")
        assertTrue(runCatching {ProfileConfig.import(node.toString())}.isFailure)
    }
    @Test fun bounded_utf8_document_reader_rejects_binary_empty_invalid_and_oversize_files() {
        assertEquals(config,ProfileDocument.read(config.byteInputStream()))
        listOf(byteArrayOf(0xC3.toByte(),0x28), byteArrayOf(0), byteArrayOf(),ByteArray(ProfileConfig.MAX_BYTES+1) {65}).forEach {
            assertTrue(runCatching {ProfileDocument.read(it.inputStream())}.isFailure)
        }
    }
}
