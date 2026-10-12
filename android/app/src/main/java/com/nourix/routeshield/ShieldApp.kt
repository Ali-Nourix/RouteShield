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
    ShieldTheme(model.saved.optString("theme","system"),model.saved.optString("language","system"),model.saved.optString("themeSeed","brand")) {
        var page by remember { mutableIntStateOf(0) }
        val labels=listOf(stringResource(R.string.connection),stringResource(R.string.profiles),stringResource(R.string.applications),stringResource(R.string.settings))
        ShieldShell(page,labels,{page=it},model.message,{model.message=null}) {
            Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal=if(androidx.compose.ui.platform.LocalConfiguration.current.screenWidthDp>=600) 24.dp else ShieldSpace.gutter,vertical=16.dp),verticalArrangement=Arrangement.spacedBy(ShieldSpace.section)) {
                Text(labels[page],style=MaterialTheme.typography.headlineLarge)
                if(model.busy) ShieldBusy()
                when(page) {
                    0->ConnectionScreen(model,onConnect,onSystemVpn)
                    1->ProfilesScreen(model)
                    2->ApplicationsScreen(model)
                    else->SettingsScreen(model,onSystemVpn,onExport)
                }
                Text(stringResource(R.string.notice),style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}
@Composable private fun ConnectionScreen(model:ShieldModel,onConnect:()->Unit,onVpn:()->Unit) {
    val value=model.tunnel
    val resource=when(value.stage) {"starting"->R.string.starting;"ready"->R.string.ready;"online"->R.string.online;"waiting"->R.string.waiting;"degraded"->R.string.degraded;"error"->R.string.error;"permission"->R.string.permission;"stopping"->R.string.stopping;else->R.string.idle}
    val nodes=ProfileConfig.activeNodes(model.saved)
    ShieldPanel(emphasized=true) {
        Text(stringResource(resource),style=MaterialTheme.typography.headlineMedium)
        Text(stringResource(R.string.subtitle),style=MaterialTheme.typography.bodyMedium)
        if(value.stage in listOf("starting","ready","stopping")) ShieldBusy()
        ShieldButton(stringResource(if(value.running) R.string.disconnect else R.string.connect),onConnect,
            modifier=Modifier.fillMaxWidth(),enabled=value.stage!="stopping" && (value.running || (!model.busy && nodes.length()>0)),primary=true)
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
    ShieldPanel(compact=true) {
        Text(stringResource(R.string.kill_switch),style=MaterialTheme.typography.titleMedium)
        Text(stringResource(when(value.lockdown) {true->R.string.kill_active;false->R.string.kill_off;else->R.string.kill_unknown}),style=MaterialTheme.typography.bodyMedium)
        Text(stringResource(R.string.kill_detail),style=MaterialTheme.typography.bodySmall)
        ShieldTextAction(stringResource(R.string.kill_setup),onVpn)
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
    var visible by remember {mutableIntStateOf(20)}
    val library=model.saved.optJSONArray("library") ?: JSONArray()
    val edit=!model.tunnel.running && !model.busy
    val switchable=!model.busy && model.tunnel.stage !in setOf("starting","stopping")
    ShieldButton(stringResource(R.string.import_profiles),{importing=true},enabled=edit,primary=true,modifier=Modifier.fillMaxWidth())
    if(library.length()==0) ShieldPanel { Text(stringResource(R.string.empty),style=MaterialTheme.typography.bodyLarge) }
    else {
        val index=model.saved.optInt("active",0).coerceIn(0,library.length()-1)
        val group=library.getJSONObject(index); val nodes=group.getJSONArray("nodes")
        LaunchedEffect(index) {visible=20}
        ShieldGroupTabs((0 until library.length()).map {library.getJSONObject(it).optString("name")},index,edit) {model.select(it)}
        ShieldPanel(compact=true) {
            Text(stringResource(R.string.active_profile),style=MaterialTheme.typography.labelLarge)
            Text(group.optString("name"),style=MaterialTheme.typography.titleLarge,maxLines=2,overflow=androidx.compose.ui.text.style.TextOverflow.Ellipsis)
            Text("${nodes.length()} · "+stringResource(if(group.has("url")) R.string.subscription else R.string.manual),style=MaterialTheme.typography.bodyMedium)
            group.optJSONObject("usage")?.let { usage ->
                SubscriptionMetadata.remaining(usage)?.let { Text(stringResource(R.string.quota_left)+": "+bytes(it),style=MaterialTheme.typography.bodyMedium) }
                if(usage.optLong("expire") in 1..SubscriptionMetadata.MAX_EXPIRY) Text(stringResource(R.string.expires)+": "+Instant.ofEpochSecond(usage.getLong("expire")).atZone(ZoneId.systemDefault()).toLocalDate(),style=MaterialTheme.typography.bodySmall)
                Text(stringResource(R.string.provider_metadata),style=MaterialTheme.typography.bodySmall)
            }
            Row(horizontalArrangement=Arrangement.spacedBy(8.dp),modifier=Modifier.fillMaxWidth()) {
                if(group.has("url")) ShieldTextAction(stringResource(R.string.refresh),{model.refresh(index)},enabled=edit,modifier=Modifier.weight(1f))
                ShieldTextAction(stringResource(R.string.delete),{deletion=index},enabled=edit,modifier=Modifier.weight(1f),destructive=true)
            }
        }
        ShieldPanel(compact=true) {
            Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween,verticalAlignment=androidx.compose.ui.Alignment.CenterVertically) {
                Text(stringResource(R.string.nodes),style=MaterialTheme.typography.titleLarge,modifier=Modifier.weight(1f))
                ShieldTextAction(stringResource(R.string.test_nodes_short),{model.testNodes()},enabled=model.tunnel.running && model.tunnel.stage!="stopping" && nodes.length()>1 && !model.busy)
            }
            ShieldNodeRow(stringResource(R.string.auto_short),stringResource(R.string.auto_detail),model.tunnel.selected?.let {tag-> tag.removePrefix("node-").toIntOrNull()?.let {i->if(i in 0 until nodes.length()) nodes.getJSONObject(i).optString("tag") else null} },model.saved.optInt("selected")==0,switchable) {model.select(index,0)}
            ShieldRule()
            val order=(0 until nodes.length()).toList().let {list->if(model.saved.optBoolean("sortLatency")) list.sortedBy {model.tunnel.delays["node-$it"] ?: Int.MAX_VALUE} else list}
            order.take(visible).forEach {node->
                val item=nodes.getJSONObject(node); val delay=model.tunnel.delays["node-$node"]
                ShieldNodeRow(item.optString("tag"),item.optString("type").uppercase(),delay?.let {"$it ms"} ?: "—",
                    model.saved.optInt("selected")==node+1,switchable) {model.select(index,node+1)}

            }
            if(visible<order.size) ShieldButton(stringResource(R.string.show_more),{visible+=20},modifier=Modifier.fillMaxWidth())
            Text(stringResource(R.string.nodes_hint),style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
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
    val edit=!model.tunnel.running && !model.busy
    ShieldPanel {
        Text(stringResource(R.string.app_vpn_title),style=MaterialTheme.typography.titleLarge)
        Text(stringResource(R.string.app_vpn_detail),style=MaterialTheme.typography.bodyMedium)
        if(model.saved.optString("mode","all")=="include") Text(stringResource(R.string.legacy_include),style=MaterialTheme.typography.bodySmall)
        ShieldSetting(stringResource(R.string.auto_iranian_apps),stringResource(R.string.auto_iranian_detail),model.saved.optBoolean("autoDomesticApps"),{model.settings {put("autoDomesticApps",it)}},edit && model.saved.optString("mode","all")!="include")
        Text(stringResource(R.string.catalog_coverage,applications.count {it.first in AppRoutingPolicy.domesticPackages},AppRoutingPolicy.domesticPackages.size),style=MaterialTheme.typography.bodySmall)
        ShieldTextAction(stringResource(R.string.reset_app_policy),{model.settings {put("mode","all").put("apps",JSONArray()).put("forceVpnApps",JSONArray()).put("autoDomesticApps",false)}},enabled=edit)
    }
    Text(stringResource(R.string.apps_hint),style=MaterialTheme.typography.bodySmall)
    ShieldField(query,{query=it},stringResource(R.string.search_apps))
    ShieldPanel {
        val filtered=applications.filter {query.isBlank() || it.first.contains(query,true) || it.second.contains(query,true)}
        if(filtered.isEmpty()) Text(stringResource(R.string.no_apps))
        filtered.forEach {(pkg,label)->
            val on=AppRoutingPolicy.enabled(model.saved,pkg)
            val detail=stringResource(if(on) R.string.app_vpn_on else if(AppRoutingPolicy.isAutomatic(model.saved,pkg)) R.string.app_auto_direct else R.string.app_manual_direct)
            ShieldAppSwitch(label,pkg,detail,on,edit) {checked->model.settings {AppRoutingPolicy.change(this,pkg,checked)}}

        }
    }
}
@Composable private fun SettingsScreen(model:ShieldModel,onVpn:()->Unit,onExport:()->Unit) {
    val edit=!model.tunnel.running && !model.busy
    ShieldPanel {
        Text(stringResource(R.string.appearance),style=MaterialTheme.typography.titleLarge)
        ShieldChoices(listOf(stringResource(R.string.system),stringResource(R.string.light),stringResource(R.string.dark)),listOf("system","light","dark").indexOf(model.saved.optString("theme","system")).coerceAtLeast(0),{model.settings {put("theme",listOf("system","light","dark")[it])}},enabled=!model.busy)
        ShieldChoices(listOf("System","فارسی","English"),listOf("system","fa","en").indexOf(model.saved.optString("language","system")).coerceAtLeast(0),{model.settings {put("language",listOf("system","fa","en")[it])}},enabled=!model.busy)
        Text(stringResource(R.string.theme_color),style=MaterialTheme.typography.titleMedium)
        ShieldColors(model.saved.optString("themeSeed","brand"),listOf(R.string.color_red,R.string.color_blue,R.string.color_green,R.string.color_purple,R.string.color_teal,R.string.color_pink,R.string.color_amber,R.string.color_slate).map {stringResource(it)},!model.busy) {key->model.settings {put("themeSeed",key)}}

    }
    ShieldPanel {
        Text(stringResource(R.string.security),style=MaterialTheme.typography.titleLarge)
        ShieldSetting(stringResource(R.string.secure_dns),stringResource(R.string.dns_detail),model.saved.optBoolean("secureDns",true),{model.settings {put("secureDns",it)}},edit)
        ShieldSetting(stringResource(R.string.fragment_tls),stringResource(R.string.fragment_detail),model.saved.optBoolean("fragmentTls"),{model.settings {put("fragmentTls",it)}},edit)
        ShieldSetting(stringResource(R.string.refuse_quic),stringResource(R.string.quic_detail),model.saved.optBoolean("refuseQuic",true),{model.settings {put("refuseQuic",it)}},edit)
        ShieldSetting(stringResource(R.string.domestic),stringResource(R.string.domestic_detail),model.saved.optBoolean("domestic"),{model.settings {put("domestic",it)}},edit)
        ShieldSetting(stringResource(R.string.prefer_stability),stringResource(R.string.stability_detail),model.saved.optBoolean("preferStability",true),{model.settings {put("preferStability",it)}},edit)
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
