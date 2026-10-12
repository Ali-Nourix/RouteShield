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
    private fun io(action: suspend ()->JSONObject) {
        if (busy) return
        busy=true
        viewModelScope.launch { try { saved=withContext(Dispatchers.IO) { action() }; revision++ }
            catch(e:Exception) { message="Operation failed. Check the input, network, or local key." }
            finally { busy=false } }
    }
    fun import(name:String, input:String) = io {
        val data=JSONObject(saved.toString()); val url=input.trim().takeIf { it.startsWith("https://") }
        val result=url?.let(SubscriptionFetch::fetch)
        val nodes=ProfileConfig.import(result?.body ?: input)
        val entry=JSONObject().put("id",UUID.randomUUID().toString()).put("name",name.trim().take(80).ifBlank { "Import" }).put("nodes",nodes)
        if(url!=null) entry.put("url",url).put("usage",result!!.usage).put("updated",System.currentTimeMillis())
        data.getJSONArray("library").put(entry); data.put("active",data.getJSONArray("library").length()-1).put("selected",0)
        store.save(data); data
    }
    fun refresh(index:Int) = io {
        val data=JSONObject(saved.toString()); val item=data.getJSONArray("library").getJSONObject(index)
        val result=SubscriptionFetch.fetch(item.getString("url")); val nodes=ProfileConfig.import(result.body)
        item.put("nodes",nodes).put("usage",result.usage).put("updated",System.currentTimeMillis()); data.put("selected",0)
        store.save(data); data
    }
    fun select(group:Int, node:Int=0) = io { val data=JSONObject(saved.toString()).put("active",group).put("selected",node); store.save(data); data }
    fun delete(index:Int) = io { val data=JSONObject(saved.toString()); data.getJSONArray("library").remove(index); data.put("active",0).put("selected",0); store.save(data); data }
    fun settings(update:JSONObject.()->Unit) = io { val data=JSONObject(saved.toString()).apply(update); store.save(data); data }
    fun clear() = io { store.clear(); JSONObject().put("library",JSONArray()) }
    fun event(text:String) { events.add("${Instant.now()}  $text"); while(events.size>100) events.removeAt(0) }
    fun testNodes() {
        if(!tunnel.running || busy) return
        viewModelScope.launch { runCatching { withContext(Dispatchers.IO) { Libbox.newStandaloneCommandClient().urlTest("proxy") } }
            .onSuccess { event("Requested native node measurements") }.onFailure { message="Node test requires an active automatic group." } }
    }
    fun diagnostics():String = "RouteShield Android ${BuildConfig.VERSION_NAME}\nVariant: ${BuildConfig.BUILD_TYPE}\nState: ${tunnel.stage}\n" +
        "Library groups: ${saved.optJSONArray("library")?.length() ?: 0}\nNo profiles, subscription URLs, keys, destinations or request bodies included.\n"+events.joinToString("\n")
    override fun onCleared() { TunnelStatus.listeners.remove(observer); super.onCleared() }
}
