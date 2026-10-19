package com.nourix.routeshield

import android.app.Application
import android.os.Handler
import android.os.Looper
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.compose.runtime.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import io.nekohasekai.libbox.Libbox
import java.time.Instant
import java.util.UUID

class ShieldModel(application: Application) : AndroidViewModel(application) {
    private val store=ProfileStore(application)
    var saved by mutableStateOf(JSONObject().put("library",JSONArray())); private set
    var tunnel by mutableStateOf(TunnelStatus.value); private set
    var busy by mutableStateOf(false); private set
    var message by mutableStateOf<String?>(null)
    var revision by mutableIntStateOf(0); private set
    val events=mutableStateListOf<String>()
    private val observer: (TunnelSnapshot)->Unit = { value -> Handler(Looper.getMainLooper()).post { tunnel=value } }
    init { TunnelStatus.listeners.add(observer); io { val data=store.load(); if(!data.has("library")) {
        val old=data.optJSONArray("nodes"); val list=JSONArray(); if(old!=null && old.length()>0) list.put(JSONObject().put("id",UUID.randomUUID().toString()).put("name","Imported").put("nodes",old))
        data.put("library",list).remove("nodes") }; data } }
    private fun io(errorResource:Int=R.string.operation_error, onSuccess:()->Unit={}, onFailure:((String)->Unit)?=null, action: suspend ()->JSONObject) {
        if (busy) return
        busy=true
        viewModelScope.launch { try { saved=withContext(Dispatchers.IO) { action() }; revision++; onSuccess() }
            catch(e:kotlinx.coroutines.CancellationException) {throw e}
            catch(e:Exception) { val error=text(errorResource); if(onFailure!=null) onFailure(error) else message=error }
            finally { busy=false } }
    }
    private fun text(resource:Int):String {
        val app=getApplication<Application>();val language=saved.optString("language","system")
        if(language=="system") return app.getString(resource)
        val config=android.content.res.Configuration(app.resources.configuration).apply {setLocale(java.util.Locale.forLanguageTag(language))}
        return app.createConfigurationContext(config).getString(resource)
    }
    fun import(name:String, input:String, onFailed:((String)->Unit)?=null, onImported:()->Unit={}) = io(R.string.import_error,onImported,onFailed) { addProfile(name,input) }
    fun importDocument(uri:android.net.Uri) = viewModelScope.launch {
        // Activity results may arrive while restored encrypted preferences are still loading.
        while(busy) kotlinx.coroutines.delay(50)
        io(R.string.import_file_error,{message=text(R.string.imported_file)}) {
            check(!tunnel.running) { "Disconnect before importing" }
            val resolver=getApplication<Application>().contentResolver
            val input=resolver.openInputStream(uri)?.use(ProfileDocument::read) ?: error("Cannot open profile")
            val name=runCatching {resolver.query(uri,arrayOf(android.provider.OpenableColumns.DISPLAY_NAME),null,null,null)?.use {
                if(it.moveToFirst()) it.getString(0)?.substringBeforeLast('.')?.take(80) else null
            }}.getOrNull().orEmpty()
            addProfile(name,input)
        }
    }
    private fun addProfile(name:String,input:String):JSONObject {
        check(!tunnel.running) { "Disconnect before importing" }
        val data=JSONObject(saved.toString()); val url=input.trim().takeIf { it.startsWith("https://") }
        val result=url?.let(SubscriptionFetch::fetch)
        val nodes=ProfileConfig.import(result?.body ?: input)
        val entry=JSONObject().put("id",UUID.randomUUID().toString()).put("name",name.filterNot(Char::isISOControl).trim().take(80).ifBlank { "Import" }).put("nodes",nodes)
        if(url!=null) entry.put("url",url).put("usage",result!!.usage).put("updated",System.currentTimeMillis())
        data.getJSONArray("library").put(entry); data.put("active",data.getJSONArray("library").length()-1).put("selected",0)
        store.save(data); return data
    }
    fun refresh(index:Int) = io {
        val data=JSONObject(saved.toString()); val item=data.getJSONArray("library").getJSONObject(index)
        val result=SubscriptionFetch.fetch(item.getString("url")); val nodes=ProfileConfig.import(result.body)
        item.put("nodes",nodes).put("usage",result.usage).put("updated",System.currentTimeMillis()); data.put("selected",0)
        store.save(data); data
    }
    fun selectGroup(group:Int,onSelected:()->Unit={}) {
        val library=saved.optJSONArray("library") ?: return
        if(group !in 0 until library.length()) return
        val item=library.getJSONObject(group)
        select(group,item.optInt("selected",0).coerceIn(0,item.getJSONArray("nodes").length()),onSelected)
    }
    fun select(group:Int,node:Int=0,onSelected:()->Unit={}) = io(R.string.switch_error,onSelected) {
        val previous=JSONObject(saved.toString());val data=JSONObject(saved.toString())
        val library=data.getJSONArray("library")
        require(group in 0 until library.length())
        val nodes=library.getJSONObject(group).getJSONArray("nodes")
        val tag=ProfileConfig.selectionTag(nodes.length(),node)
        val switching=group!=data.optInt("active",0)
        val previousGroup=data.optInt("active",0)
        if(previousGroup in 0 until library.length()) library.getJSONObject(previousGroup).put("selected",data.optInt("selected",0))
        data.put("active",group).put("selected",node);library.getJSONObject(group).put("selected",node)
        if(tunnel.running) {
            require(tunnel.stage in setOf("ready","online","waiting","degraded"))
            if(switching) {
                val config=ProfileConfig.runtime(nodes,node,data)
                store.save(data)
                try {TunnelService.replaceProfile(config,node==0 && nodes.length()>1)}
                catch(e:Exception) {store.save(previous);throw e}
            } else {
                Libbox.newStandaloneCommandClient().selectOutbound("proxy",tag)
                TunnelService.selectionChanged(node==0 && nodes.length()>1)
                store.save(data)
            }
        } else store.save(data)
        data
    }
    fun delete(index:Int) = io { val data=JSONObject(saved.toString()); data.getJSONArray("library").remove(index); data.put("active",0).put("selected",0); store.save(data); data }
    fun settings(update:JSONObject.()->Unit) = io { val data=JSONObject(saved.toString()).apply(update); store.save(data); data }
    fun clear() = io { store.clear(); JSONObject().put("library",JSONArray()) }
    fun event(text:String) { events.add("${Instant.now()}  $text"); while(events.size>100) events.removeAt(0) }
    fun testNodes() {
        if(!tunnel.running || busy) return
        viewModelScope.launch { runCatching { withContext(Dispatchers.IO) { Libbox.newStandaloneCommandClient().urlTest("auto") } }
            .onSuccess { event("Requested native node measurements") }.onFailure { message="Node test requires an active automatic group." } }
    }
    fun diagnostics():String = "RouteShield Android ${BuildConfig.VERSION_NAME}\nVariant: ${BuildConfig.BUILD_TYPE}\nCommit: ${BuildConfig.SOURCE_COMMIT}\nState: ${tunnel.stage}\n" +
        "Library groups: ${saved.optJSONArray("library")?.length() ?: 0}\nHTTPS checks: ${tunnel.httpsChecks ?: "unknown"}/2\nDNS verified: ${tunnel.dnsVerified ?: "unknown"}\nApp routing: ${saved.optString("mode","all")}\nNo profiles, subscription URLs, keys, destinations or request bodies included.\n"+events.joinToString("\n")
    override fun onCleared() { TunnelStatus.listeners.remove(observer); super.onCleared() }
}
