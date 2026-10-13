package com.nourix.routeshield

import org.json.JSONArray
import org.json.JSONObject
import java.net.URI
import java.net.URLDecoder
import java.nio.charset.StandardCharsets
import java.util.Base64
import java.util.UUID

/** Imports nodes only, never another app's arbitrary inbounds, scripts or API configuration. */
object ProfileConfig {
    const val MAX_BYTES = 5 * 1024 * 1024
    const val MAX_NODES = 500
    private val types = setOf("vless", "vmess", "trojan", "shadowsocks", "socks", "http", "hysteria2", "tuic", "anytls", "wireguard")
    fun decode(value: String): String = URLDecoder.decode(value.replace("+", "%2B"), "UTF-8")
    private fun base64(value: String): String = String(runCatching { Base64.getDecoder().decode(value) }.getOrElse { Base64.getUrlDecoder().decode(value) }, StandardCharsets.UTF_8)

    fun import(text: String): JSONArray {
        require(text.toByteArray().size <= MAX_BYTES) { "Import is larger than 5 MB" }
        val source = text.trim().removePrefix("\uFEFF")
        require(source.isNotEmpty()) { "The profile is empty" }
        val nodes = JSONArray()
        if (source.startsWith("[Interface]", true)) return JSONArray().put(wireGuard(source))
        if (source.startsWith("{")) {
            val root = JSONObject(source)
            val entries = root.optJSONArray("outbounds") ?: if (root.has("endpoints")) JSONArray() else JSONArray().put(root)
            root.optJSONArray("endpoints")?.let { endpoints -> for (i in 0 until endpoints.length()) entries.put(endpoints.getJSONObject(i)) }
            require(entries.length() <= MAX_NODES) { "Import contains more than 500 nodes" }
            for (i in 0 until entries.length()) {
                val node = entries.getJSONObject(i)
                if (node.optString("type") in types) nodes.put(validate(JSONObject(node.toString())))
            }
        } else {
            val body = if (!source.contains("://")) runCatching { base64(source.filterNot(Char::isWhitespace)) }.getOrDefault(source) else source
            if (body.trimStart().startsWith("{")) return import(body)
            val seen = mutableSetOf<String>()
            body.lineSequence().map(String::trim).filter { it.isNotBlank() && !it.startsWith('#') }.forEach {
                if (seen.add(it)) {
                    require(nodes.length() < MAX_NODES) { "Import contains more than 500 nodes" }
                    nodes.put(parseLink(it))
                }
            }
        }
        require(nodes.length() in 1..MAX_NODES) { "No supported proxy nodes were found" }
        return nodes
    }

    fun parseLink(text: String): JSONObject {
        if (text.startsWith("vmess://", true)) {
            val vm = JSONObject(base64(text.substringAfter("://").substringBefore('#')))
            val node = JSONObject().put("type", "vmess").put("server", vm.getString("add"))
                .put("server_port", vm.get("port").toString().toInt()).put("uuid", vm.getString("id"))
                .put("security", "auto").put("alter_id", vm.optInt("aid", 0)).put("tag", vm.optString("ps", "VMess"))
            val q = mapOf("security" to if (vm.optString("tls") == "tls") "tls" else "none",
                "sni" to vm.optString("sni", vm.optString("host")), "type" to vm.optString("net", "tcp"),
                "host" to vm.optString("host"), "path" to vm.optString("path", "/"))
            transport(node, q); tls(node, q, false)
            return validate(node)
        }
        // Normalize SIP002 SS credentials before URI parsing. Fragments remain labels only.
        val uri = URI(text)
        val q = uri.rawQuery.orEmpty().split('&').filter(String::isNotBlank).associate {
            val parts = it.split('=', limit = 2); decode(parts[0]) to decode(parts.getOrElse(1) { "" })
        }
        val kind = when (uri.scheme.lowercase()) {
            "ss" -> "shadowsocks"; "hy2" -> "hysteria2"; else -> uri.scheme.lowercase()
        }
        require(kind in types && kind !in setOf("http", "socks")) { "Unsupported link; use a sing-box outbound JSON for this format" }
        val node = JSONObject().put("type", kind).put("server", uri.host ?: error("Missing server"))
            .put("server_port", if (uri.port > 0) uri.port else 443).put("tag", decode(uri.rawFragment ?: kind.uppercase()))
        val credentials = decode(uri.rawUserInfo ?: error("Missing credentials"))
        when (kind) {
            "vless" -> { UUID.fromString(credentials); node.put("uuid", credentials).put("packet_encoding", "xudp"); q["flow"]?.let { node.put("flow", it) } }
            "trojan", "anytls", "hysteria2" -> node.put("password", credentials)
            "tuic" -> {
                val parts = credentials.split(':', limit = 2); require(parts.size == 2) { "TUIC credentials are incomplete" }
                UUID.fromString(parts[0]); node.put("uuid", parts[0]).put("password", parts[1]).put("congestion_control", q["congestion_control"] ?: "bbr")
            }
            "shadowsocks" -> {
                require(!q.containsKey("plugin")) { "Shadowsocks plugins are not supported in this preview" }
                val plain = if (credentials.contains(':')) credentials else base64(credentials)
                val parts = plain.split(':', limit = 2); require(parts.size == 2) { "Shadowsocks credentials are incomplete" }
                node.put("method", parts[0]).put("password", parts[1])
            }
        }
        transport(node, q); tls(node, q, kind in setOf("trojan", "anytls", "hysteria2", "tuic"))
        return validate(node)
    }

    private fun tls(node: JSONObject, q: Map<String, String>, default: Boolean) {
        require(q["allowInsecure"] !in setOf("1", "true") && q["insecure"] !in setOf("1", "true")) { "Insecure TLS is not accepted" }
        val security = q["security"] ?: if (default) "tls" else "none"
        require(security in setOf("tls", "reality", "none", "")) { "Unsupported transport security" }
        if (security in setOf("tls", "reality")) {
            val t = JSONObject().put("enabled", true).put("server_name", q["sni"].takeUnless { it.isNullOrBlank() } ?: node.getString("server"))
            q["alpn"]?.takeIf(String::isNotBlank)?.let { t.put("alpn", JSONArray(it.split(','))) }
            if (node.getString("type") !in setOf("hysteria2", "tuic")) t.put("utls", JSONObject().put("enabled", true).put("fingerprint", q["fp"] ?: "chrome"))
            if (security == "reality") t.put("reality", JSONObject().put("enabled", true).put("public_key", q["pbk"] ?: error("Reality public key is missing")).put("short_id", q["sid"] ?: ""))
            node.put("tls", t)
        }
    }

    private fun transport(node: JSONObject, q: Map<String, String>) {
        val type = q["type"] ?: "tcp"
        if (type == "tcp" || type.isBlank() || node.getString("type") in setOf("hysteria2", "tuic", "shadowsocks")) return
        val t = JSONObject().put("type", type)
        when (type) {
            "ws", "httpupgrade" -> { t.put("path", q["path"] ?: "/"); q["host"]?.takeIf(String::isNotBlank)?.let { if (type == "ws") t.put("headers", JSONObject().put("Host", it)) else t.put("host", it) } }
            "grpc" -> t.put("service_name", q["serviceName"] ?: "")
            "http" -> { t.put("path", q["path"] ?: "/"); q["host"]?.let { t.put("host", JSONArray().put(it)) } }
            else -> error("Unsupported transport")
        }
        node.put("transport", t)
    }

    fun validate(node: JSONObject): JSONObject {
        require(node.optString("type") in types) { "Unsupported outbound" }
        if (node.optString("type") != "wireguard") {
            require(node.optString("server").isNotBlank() && node.optString("server").length <= 253) { "Server is missing" }
            require(node.optInt("server_port") in 1..65535) { "Server port is invalid" }
        } else {
            require((node.optJSONArray("address")?.length() ?: 0) > 0) { "WireGuard addresses are missing" }
            require(node.optString("private_key").isNotBlank() && (node.optJSONArray("peers")?.length() ?: 0) > 0) { "WireGuard keys or peers are missing" }
            node.put("system", false).put("name", "RouteShield-WG")
        }
        require(node.optJSONObject("tls")?.optBoolean("insecure") != true) { "Insecure TLS is not accepted" }
        // Disallow imported chaining and inbound/API behavior. Routing policy belongs to this app.
        listOf("detour", "bind_interface", "routing_mark", "domain_resolver").forEach(node::remove)
        node.put("domain_resolver", "dns-local")
        node.put("tag", node.optString("tag").take(128))
        if (node.optString("tag").isBlank()) node.put("tag", node.getString("type").uppercase())
        return node
    }

    fun activeNodes(saved: JSONObject): JSONArray {
        val library = saved.optJSONArray("library")
        return if (library != null && library.length() > 0) library.getJSONObject(saved.optInt("active", 0).coerceIn(0, library.length()-1)).getJSONArray("nodes")
            else saved.optJSONArray("nodes") ?: JSONArray()
    }

    private fun wireGuard(text: String): JSONObject {
        val iface = mutableMapOf<String,String>(); val peers = mutableListOf<MutableMap<String,String>>()
        var section: MutableMap<String,String>? = null
        text.lineSequence().map { it.substringBefore('#').trim() }.filter(String::isNotEmpty).forEach { line ->
            when {
                line.equals("[Interface]", true) -> section = iface
                line.equals("[Peer]", true) -> section = mutableMapOf<String,String>().also(peers::add)
                '=' in line -> { val parts = line.split('=', limit=2); section?.set(parts[0].trim().lowercase(), parts[1].trim()) }
            }
        }
        require(listOf("jc","jmin","jmax","s1","s2","h1","h2","h3","h4").none(iface::containsKey)) { "AmneziaWG requires a different mobile engine" }
        val addresses = iface["address"]?.split(',')?.map(String::trim) ?: error("WireGuard addresses missing")
        val peerArray = JSONArray()
        peers.forEach { fields ->
            val endpoint = URI("udp://" + (fields["endpoint"] ?: error("WireGuard endpoint missing")))
            require(endpoint.port in 1..65535) { "WireGuard port invalid" }
            val peer = JSONObject().put("address", endpoint.host.removePrefix("[").removeSuffix("]")).put("port", endpoint.port)
                .put("public_key", fields["publickey"] ?: error("Peer public key missing"))
                .put("allowed_ips", JSONArray((fields["allowedips"] ?: "0.0.0.0/0,::/0").split(',').map(String::trim)))
            fields["presharedkey"]?.let { peer.put("pre_shared_key",it) }
            fields["persistentkeepalive"]?.toIntOrNull()?.let { peer.put("persistent_keepalive_interval",it) }
            peerArray.put(peer)
        }
        return validate(JSONObject().put("type","wireguard").put("tag","WireGuard").put("address",JSONArray(addresses))
            .put("private_key",iface["privatekey"] ?: error("Private key missing")).put("peers",peerArray)
            .put("mtu",iface["mtu"]?.toIntOrNull() ?: 1408))
    }

    fun selectionTag(nodeCount:Int, selected:Int):String {
        require(nodeCount>0 && selected in 0..nodeCount) { "Choose a valid node" }
        return if(selected==0 && nodeCount>1) "auto" else "node-${if(selected==0) 0 else selected-1}"
    }

    fun runtime(nodes: JSONArray, selected: Int, settings: JSONObject = JSONObject()): String {
        require(nodes.length() > 0 && selected in 0..nodes.length()) { "Choose a valid profile" }
        val out = JSONArray(); val endpoints = JSONArray(); val tags = JSONArray()
        (0 until nodes.length()).forEach { source ->
            val n = validate(JSONObject(nodes.getJSONObject(source).toString())).put("tag", "node-$source")
            if (settings.optBoolean("fragmentTls", false) && n.optString("type") !in setOf("hysteria2","tuic","wireguard"))
                n.optJSONObject("tls")?.put("fragment",true)?.put("record_fragment",true)
            if (n.optString("type") == "wireguard") endpoints.put(n) else out.put(n)
            tags.put(n.getString("tag"))
        }
        val choices=JSONArray()
        if(nodes.length()>1) {
            out.put(JSONObject().put("type", "urltest").put("tag", "auto").put("outbounds", tags)
                .put("url", "https://www.gstatic.com/generate_204").put("interval", if(settings.optBoolean("preferStability",true)) "45s" else "30s")
                .put("tolerance", if(settings.optBoolean("preferStability",true)) 150 else 0).put("interrupt_exist_connections", false))
            choices.put("auto")
        }
        for(i in 0 until tags.length()) choices.put(tags.getString(i))
        out.put(JSONObject().put("type","selector").put("tag","proxy").put("outbounds",choices)
            .put("default",selectionTag(nodes.length(),selected)).put("interrupt_exist_connections",true))
        out.put(JSONObject().put("type", "direct").put("tag", "direct"))
        val root = JSONObject().put("log", JSONObject().put("disabled", true))
            .put("dns", JSONObject().put("servers", JSONArray()
                .put(JSONObject().put("type", "local").put("tag", "dns-local"))
                .put(JSONObject().put("type", "https").put("tag", "dns-remote").put("server", "1.1.1.1").put("detour", "proxy")))
                .put("final", if (settings.optBoolean("secureDns",true)) "dns-remote" else "dns-local").put("strategy", "prefer_ipv4"))
            .put("inbounds", JSONArray().put(JSONObject().put("type", "tun").put("tag", "tun")
                .put("address", JSONArray().put("172.19.0.1/30").put("fdfe:dcba:9876::1/126"))
                .put("mtu", 1400).put("auto_route", true).put("strict_route", true).put("stack", "mixed")))
            .put("outbounds", out)
            .put("route", JSONObject().put("auto_detect_interface", true).put("default_domain_resolver", JSONObject().put("server", "dns-local"))
                .put("rules", JSONArray().put(JSONObject().put("action", "sniff"))
                    .put(JSONObject().put("protocol", "dns").put("action", "hijack-dns")))
                .put("final", "proxy"))
        if (endpoints.length() > 0) root.put("endpoints", endpoints)
        val rules = root.getJSONObject("route").getJSONArray("rules")
        if (settings.optBoolean("domestic",false)) rules.put(JSONObject().put("domain_suffix",JSONArray().put(".ir").put("digikala.com").put("aparat.com").put("filimo.com").put("snapp.ir")).put("action","route").put("outbound","direct"))
        if (settings.optBoolean("refuseQuic",true)) rules.put(JSONObject().put("network","udp").put("port",JSONArray().put(443).put(80)).put("action","reject"))
        return root.toString()
    }
}
