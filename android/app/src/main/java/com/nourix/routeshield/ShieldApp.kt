package com.nourix.routeshield

import android.content.Intent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import com.nourix.routeshield.designsystem.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import java.time.ZoneId
import java.util.Locale

@Composable fun ShieldApp(model:ShieldModel,onConnect:()->Unit,onSystemVpn:()->Unit,onExport:()->Unit) {
    ShieldTheme(model.saved.optString("theme","system"),model.saved.optString("language","system")) {
        var page by remember { mutableIntStateOf(0) }
        val labels=listOf(stringResource(R.string.connection),stringResource(R.string.profiles),stringResource(R.string.applications),stringResource(R.string.settings))
        ShieldShell(page,labels,{page=it},model.message,{model.message=null}) {
            Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(ShieldSpace.gutter),verticalArrangement=Arrangement.spacedBy(ShieldSpace.section)) {
                Text(labels[page],style=MaterialTheme.typography.headlineLarge)
                if(model.busy) ShieldBusy()
                when(page) {
                    0->ConnectionScreen(model,onConnect)
                    1->ProfilesScreen(model)
                    2->ApplicationsScreen(model)
                    else->SettingsScreen(model,onSystemVpn,onExport)
                }
                Text(stringResource(R.string.notice),style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}
@Composable private fun ConnectionScreen(model:ShieldModel,onConnect:()->Unit) {
    val value=model.tunnel
    val resource=when(value.stage) {"starting"->R.string.starting;"ready"->R.string.ready;"online"->R.string.online;"waiting"->R.string.waiting;"error"->R.string.error;"permission"->R.string.permission;else->R.string.idle}
    val nodes=ProfileConfig.activeNodes(model.saved)
    ShieldPanel(emphasized=true) {
        Text(stringResource(resource),style=MaterialTheme.typography.headlineMedium)
        Text(stringResource(R.string.subtitle),style=MaterialTheme.typography.bodyMedium)
        if(value.stage in listOf("starting","ready")) ShieldBusy()
        ShieldButton(stringResource(if(value.running) R.string.disconnect else R.string.connect),onConnect,
            modifier=Modifier.fillMaxWidth(),enabled=value.running || (!model.busy && nodes.length()>0),primary=true)
    }
    ShieldPanel {
        Text(stringResource(R.string.live_measurements),style=MaterialTheme.typography.titleLarge)
        Metric(stringResource(R.string.response_time),value.latencyMs?.let {"$it ms"} ?: "—")
        Metric(stringResource(R.string.upload),value.up?.let {bytes(it)+"/s"} ?: "—")
        Metric(stringResource(R.string.download),value.down?.let {bytes(it)+"/s"} ?: "—")
        Metric(stringResource(R.string.total_transfer),if(value.totalUp!=null && value.totalDown!=null) bytes(value.totalUp+value.totalDown) else "—")
        Metric(stringResource(R.string.exit_ip),value.exitIp ?: "—")
        Text(stringResource(R.string.metric_source),style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
    }
    Text(stringResource(R.string.safety),style=MaterialTheme.typography.bodyMedium,color=MaterialTheme.colorScheme.onSurfaceVariant)
}
@Composable private fun Metric(label:String,value:String) {
    Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween) {
        Text(label,modifier=Modifier.weight(1f),style=MaterialTheme.typography.bodyLarge)
        CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) { Text(value,modifier=Modifier.widthIn(max=180.dp),style=MaterialTheme.typography.titleMedium,fontFamily=FontFamily.Monospace) }
    }
}
private fun bytes(value:Long):String = if(value>=1024*1024) String.format(Locale.ROOT,"%.1f MiB",value/1048576.0) else String.format(Locale.ROOT,"%.1f KiB",value/1024.0)
@Composable private fun ProfilesScreen(model:ShieldModel) {
    var importing by remember { mutableStateOf(false) }; var deletion by remember {mutableIntStateOf(-1)}
    val library=model.saved.optJSONArray("library") ?: JSONArray()
    val edit=!model.tunnel.running && !model.busy
    ShieldButton(stringResource(R.string.import_profiles),{importing=true},enabled=edit,primary=true,modifier=Modifier.fillMaxWidth())
    if(library.length()==0) ShieldPanel { Text(stringResource(R.string.empty)) }
    for(index in 0 until library.length()) {
        val group=library.getJSONObject(index); val nodes=group.getJSONArray("nodes")
        val active=model.saved.optInt("active",0)==index
        ShieldPanel(emphasized=active) {
            Text(group.optString("name"),style=MaterialTheme.typography.titleLarge)
            Text("${nodes.length()} · "+stringResource(if(group.has("url")) R.string.subscription else R.string.manual),style=MaterialTheme.typography.bodyMedium)
            if(group.has("usage")) {
                val usage=group.getJSONObject("usage")
                if(usage.has("total") && usage.optLong("total")>0) {
                    val left=SubscriptionMetadata.remaining(usage) ?: 0L
                    Text(stringResource(R.string.quota_left)+": "+bytes(left))
                }
                if(usage.optLong("expire") in 1..SubscriptionMetadata.MAX_EXPIRY) Text(stringResource(R.string.expires)+": "+Instant.ofEpochSecond(usage.getLong("expire")).atZone(ZoneId.systemDefault()).toLocalDate())
                Text(stringResource(R.string.provider_metadata),style=MaterialTheme.typography.bodySmall)
            }
            ShieldButton(stringResource(if(active) R.string.rs_selected else R.string.select),{model.select(index)},enabled=edit && !active)
            if(group.has("url")) ShieldButton(stringResource(R.string.refresh),{model.refresh(index)},enabled=edit)
            ShieldButton(stringResource(R.string.delete),{deletion=index},enabled=edit)
            if(active) {
                ShieldRule()
                ShieldButton(stringResource(R.string.auto),{model.select(index,0)},enabled=edit)
                ShieldButton(stringResource(R.string.test_nodes),{model.testNodes()},enabled=model.tunnel.running && model.saved.optInt("selected")==0 && nodes.length()>1)
                val order=(0 until nodes.length()).toList().let { list -> if(model.saved.optBoolean("sortLatency")) list.sortedBy {model.tunnel.delays["node-$it"] ?: Int.MAX_VALUE} else list }
                order.forEach { node ->
                    val item=nodes.getJSONObject(node); val delay=model.tunnel.delays["node-$node"]
                    Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
                        Text(item.optString("tag"),style=MaterialTheme.typography.titleMedium)
                        Text(item.optString("type").uppercase()+" · "+(delay?.let {"$it ms"} ?: stringResource(R.string.unmeasured)),style=MaterialTheme.typography.bodySmall)
                        ShieldButton(stringResource(if(model.saved.optInt("selected")==node+1) R.string.rs_selected else R.string.select),{model.select(index,node+1)},enabled=edit)
                    }
                    ShieldRule()
                }
            }
        }
    }
    if(importing) ImportDialog(model) {importing=false}
    if(deletion>=0) ShieldConfirm(stringResource(R.string.delete),stringResource(R.string.delete_profile_prompt),stringResource(R.string.delete),stringResource(R.string.cancel),
        {model.delete(deletion);deletion=-1},{deletion=-1})
}
@Composable private fun ImportDialog(model:ShieldModel,onDismiss:()->Unit) {
    var name by remember {mutableStateOf("")}; var input by remember {mutableStateOf("")}
    ShieldDialog(stringResource(R.string.import_profiles),onDismiss,secure=true) {
        Column(Modifier.weight(1f,false).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(16.dp)) {
            ShieldField(name,{name=it},stringResource(R.string.profile_name))
            ShieldField(input,{input=it},stringResource(R.string.import_hint),singleLine=false)
            Text(stringResource(R.string.subscription_hint),style=MaterialTheme.typography.bodySmall)
        }
        ShieldButton(stringResource(R.string.save),{model.import(name,input);input="";onDismiss()},enabled=input.isNotBlank(),primary=true)
        ShieldButton(stringResource(R.string.cancel),onDismiss)
    }
}
@Composable private fun ApplicationsScreen(model:ShieldModel) {
    val context=LocalContext.current; var applications by remember {mutableStateOf(emptyList<Pair<String,String>>())}; var query by remember {mutableStateOf("")}
    LaunchedEffect(context) { applications=withContext(Dispatchers.IO) {
        @Suppress("DEPRECATION") val list=context.packageManager.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER),0)
        list.distinctBy {it.activityInfo.packageName}.filter {it.activityInfo.packageName!=context.packageName}.map {it.activityInfo.packageName to it.loadLabel(context.packageManager).toString()}.sortedBy {it.second.lowercase()}
    } }
    val modes=listOf("all","include","exclude"); val edit=!model.tunnel.running && !model.busy
    ShieldChoices(listOf(stringResource(R.string.all),stringResource(R.string.include),stringResource(R.string.exclude)),modes.indexOf(model.saved.optString("mode","all")).coerceAtLeast(0),{index->model.settings {put("mode",modes[index])}},enabled=edit)
    Text(stringResource(when(model.saved.optString("mode","all")) { "include"->R.string.include_hint; "exclude"->R.string.exclude_hint; else->R.string.all_hint }),style=MaterialTheme.typography.titleMedium)
    Text(stringResource(R.string.apps_hint),style=MaterialTheme.typography.bodyMedium)
    ShieldField(query,{query=it},stringResource(R.string.search_apps))
    val current=model.saved.optJSONArray("apps") ?: JSONArray(); val selected=(0 until current.length()).map {current.getString(it)}.toSet()
    applications.filter {query.isBlank() || it.first.contains(query,true) || it.second.contains(query,true)}.forEach { (pkg,label) ->
        ShieldCheck(label+"\n"+pkg,pkg in selected,{checked->model.settings {val next=selected.toMutableSet();if(checked) next.add(pkg) else next.remove(pkg);put("apps",JSONArray(next.toList()))}},enabled=edit)
    }
}
@Composable private fun SettingsScreen(model:ShieldModel,onVpn:()->Unit,onExport:()->Unit) {
    val edit=!model.tunnel.running && !model.busy
    ShieldPanel {
        Text(stringResource(R.string.appearance),style=MaterialTheme.typography.titleLarge)
        ShieldChoices(listOf(stringResource(R.string.system),stringResource(R.string.light),stringResource(R.string.dark)),listOf("system","light","dark").indexOf(model.saved.optString("theme","system")).coerceAtLeast(0),{model.settings {put("theme",listOf("system","light","dark")[it])}},enabled=!model.busy)
        ShieldChoices(listOf("System","فارسی","English"),listOf("system","fa","en").indexOf(model.saved.optString("language","system")).coerceAtLeast(0),{model.settings {put("language",listOf("system","fa","en")[it])}},enabled=!model.busy)
    }
    ShieldPanel {
        Text(stringResource(R.string.security),style=MaterialTheme.typography.titleLarge)
        ShieldSetting(stringResource(R.string.secure_dns),stringResource(R.string.dns_detail),model.saved.optBoolean("secureDns",true),{model.settings {put("secureDns",it)}},edit)
        ShieldSetting(stringResource(R.string.fragment_tls),stringResource(R.string.fragment_detail),model.saved.optBoolean("fragmentTls"),{model.settings {put("fragmentTls",it)}},edit)
        ShieldSetting(stringResource(R.string.refuse_quic),stringResource(R.string.quic_detail),model.saved.optBoolean("refuseQuic",true),{model.settings {put("refuseQuic",it)}},edit)
        ShieldSetting(stringResource(R.string.domestic),stringResource(R.string.domestic_detail),model.saved.optBoolean("domestic"),{model.settings {put("domestic",it)}},edit)
        ShieldSetting(stringResource(R.string.sort_latency),stringResource(R.string.latency_detail),model.saved.optBoolean("sortLatency"),{model.settings {put("sortLatency",it)}},!model.busy)
        ShieldButton(stringResource(R.string.vpn_settings),onVpn)
    }
    ShieldPanel {
        Text(stringResource(R.string.diagnostics),style=MaterialTheme.typography.titleLarge)
        Text("${BuildConfig.VERSION_NAME} · ${BuildConfig.BUILD_TYPE}",style=MaterialTheme.typography.bodyMedium)
        ShieldButton(stringResource(R.string.export_diagnostics),onExport)
        if(BuildConfig.BUILD_TYPE=="diagnostic") Text(stringResource(R.string.diagnostic_notice),style=MaterialTheme.typography.bodyMedium)
    }
}
