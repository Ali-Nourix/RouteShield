package com.nourix.routeshield

import org.json.JSONArray
import org.json.JSONObject
import java.net.Inet6Address
import java.net.InetAddress
import java.net.URI
import java.util.Base64

/** Client-only wg-quick importer. Never executes hooks or adopts host routing/DNS policy. */
object WireGuardConfig {
    fun recognizes(text:String)=text.lineSequence().map {it.substringBefore('#').trim()}
        .firstOrNull {it.isNotEmpty()}?.equals("[Interface]",true)==true

    fun parse(text:String):JSONObject {
        val iface=linkedMapOf<String,String>();val peers=mutableListOf<MutableMap<String,String>>()
        var section:MutableMap<String,String>?=null
        text.lineSequence().map {it.substringBefore('#').trim()}.filter(String::isNotEmpty).forEach {line->
            when {
                line.equals("[Interface]",true)-> { require(section==null) {"Duplicate or misplaced interface"};section=iface }
                line.equals("[Peer]",true)-> {require(iface.isNotEmpty() && peers.size<64) {"Invalid peer section"};section=linkedMapOf<String,String>().also(peers::add)}
                else-> {
                    require(!line.startsWith('[') && '=' in line && section!=null) {"Malformed WireGuard section"}
                    val (rawKey,value)=line.split('=',limit=2).map(String::trim);val key=rawKey.lowercase()
                    val allowed=if(section===iface) setOf("privatekey","address","dns","mtu","listenport")
                        else setOf("publickey","presharedkey","endpoint","allowedips","persistentkeepalive")
                    require(key in allowed && value.isNotEmpty()) {"Unsupported WireGuard setting"}
                    if(key in setOf("address","allowedips","dns") && section!!.containsKey(key)) section!![key]=section!![key]+","+value
                    else {require(!section!!.containsKey(key)) {"Duplicate WireGuard setting"};section!![key]=value}
                }
            }
        }
        require(peers.isNotEmpty()) {"WireGuard peer missing"}
        // DNS is deliberately not adopted: the import UI explains the app-owned DNS policy.
        val node=JSONObject().put("type","wireguard").put("tag","WireGuard")
            .put("address",JSONArray(csv(iface["address"]).map(::prefix)))
            .put("private_key",key(iface["privatekey"]))
            .put("mtu",number(iface["mtu"],1408,1280..65535))
        iface["listenport"]?.let {node.put("listen_port",number(it,0,0..65535))}
        val result=JSONArray()
        peers.forEach {fields->
            val uri=runCatching {URI("udp://"+fields.getValue("endpoint"))}.getOrElse {error("Invalid WireGuard endpoint")}
            require(uri.userInfo==null && uri.rawQuery==null && uri.rawFragment==null && uri.path.orEmpty().isEmpty()) {"Invalid WireGuard endpoint"}
            val peer=JSONObject().put("address",host(uri.host.orEmpty().removePrefix("[").removeSuffix("]")))
                .put("port",uri.port).put("public_key",key(fields["publickey"]))
                .put("allowed_ips",JSONArray(csv(fields["allowedips"]).map(::prefix)))
            fields["presharedkey"]?.let {peer.put("pre_shared_key",key(it))}
            fields["persistentkeepalive"]?.let {peer.put("persistent_keepalive_interval",number(it,0,0..65535))}
            result.put(peer)
        }
        return validate(node.put("peers",result))
    }

    fun validate(node:JSONObject):JSONObject {
        val addresses=node.optJSONArray("address") ?: error("WireGuard addresses missing")
        require(addresses.length() in 1..64) {"Invalid WireGuard addresses"}
        for(i in 0 until addresses.length()) addresses.put(i,prefix(addresses.getString(i)))
        node.put("private_key",key(node.optString("private_key")))
        val peers=node.optJSONArray("peers") ?: error("WireGuard peers missing")
        require(peers.length() in 1..64) {"Invalid WireGuard peers"}
        for(i in 0 until peers.length()) {
            val peer=peers.getJSONObject(i)
            peer.put("address",host(peer.optString("address")))
            require(peer.opt("port")?.toString()?.toIntOrNull()?.let {it in 1..65535}==true) {"Invalid WireGuard port"}
            peer.put("public_key",key(peer.optString("public_key")))
            if(peer.has("pre_shared_key")) peer.put("pre_shared_key",key(peer.getString("pre_shared_key")))
            val allowed=peer.optJSONArray("allowed_ips") ?: error("WireGuard allowed IPs missing")
            require(allowed.length() in 1..256) {"Invalid allowed IPs"}
            for(j in 0 until allowed.length()) allowed.put(j,prefix(allowed.getString(j)))
            if(peer.has("persistent_keepalive_interval")) require(peer.get("persistent_keepalive_interval").toString().toIntOrNull()?.let {it in 0..65535}==true) {"Invalid keepalive"}
        }
        if(node.has("mtu")) require(node.get("mtu").toString().toIntOrNull()?.let {it in 1280..65535}==true) {"Invalid WireGuard MTU"}
        if(node.has("listen_port")) require(node.get("listen_port").toString().toIntOrNull()?.let {it in 0..65535}==true) {"Invalid listen port"}
        return node.put("system",false).put("name","RouteShield-WG")
    }

    private fun csv(value:String?):List<String> {
        require(!value.isNullOrBlank()) {"WireGuard addresses or allowed IPs missing"}
        return value.split(',').map(String::trim).also {require(it.size<=256 && it.none(String::isEmpty)) {"Invalid address list"}}
    }
    private fun key(value:String?):String {
        require(value!=null && value.length==44 && runCatching {Base64.getDecoder().decode(value).size==32}.getOrDefault(false)) {"Invalid WireGuard key"}
        return value
    }
    private fun number(value:String?,default:Int,range:IntRange):Int = if(value==null) default else
        requireNotNull(value.toIntOrNull()?.takeIf {it in range}) {"Invalid WireGuard numeric setting"}
    private fun host(value:String):String {
        require(value.isNotBlank() && value.length<=253 && !value.contains('%')) {"Invalid WireGuard host"}
        if(':' in value) require(ipv6(value)) {"Invalid IPv6 endpoint"}
        else require(value.split('.').all {it.isNotEmpty() && it.length<=63 && it.first().isLetterOrDigit() && it.last().isLetterOrDigit() && it.all {c->c in 'a'..'z' || c in 'A'..'Z' || c.isDigit() || c=='-'}}) {"Invalid WireGuard host"}
        return value
    }
    private fun ipv6(value:String)=value.all {it in "0123456789abcdefABCDEF:."} && runCatching {InetAddress.getByName(value) is Inet6Address}.getOrDefault(false)
    private fun prefix(value:String):String {
        val parts=value.split('/');require(parts.size in 1..2) {"Invalid IP prefix"}
        val ip=parts[0];val v6=':' in ip
        val valid=if(v6) ipv6(ip) else ip.split('.').let {it.size==4 && it.all {part->part.isNotEmpty() && part.all(Char::isDigit) && (part.toIntOrNull() ?: -1) in 0..255}}
        require(valid) {"Expected a literal IP address"}
        val bits=if(v6) 128 else 32
        val length=if(parts.size==1) bits else requireNotNull(parts[1].toIntOrNull()) {"Invalid prefix length"}
        require(length in 0..bits) {"Invalid prefix length"}
        return "$ip/$length"
    }
}
