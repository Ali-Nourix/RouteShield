@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)
package com.nourix.routeshield.designsystem

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.material.icons.rounded.Security
import androidx.compose.material.icons.rounded.Dns
import androidx.compose.material.icons.rounded.Apps
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.unit.*
import com.materialkolor.PaletteStyle
import com.materialkolor.rememberDynamicColorScheme
import java.util.Locale

/** New RouteShield vocabulary; informed by Sekke's layering, not copied from its private source. */
object ShieldSpace { val small=8.dp; val medium=16.dp; val gutter=20.dp; val section=24.dp; val reading=680.dp }
@Composable fun ShieldTheme(mode:String="system", language:String="system", content:@Composable ()->Unit) {
    val dark=when(mode) { "dark"->true; "light"->false; else->isSystemInDarkTheme() }
    val context=LocalContext.current
    val config=android.content.res.Configuration(LocalConfiguration.current)
    if(language!="system") config.setLocale(Locale.forLanguageTag(language))
    val scoped=remember(context,language,config) { context.createConfigurationContext(config) }
    val rtl=if(config.locales[0].language in listOf("fa","ar")) LayoutDirection.Rtl else LayoutDirection.Ltr
    val colors=rememberDynamicColorScheme(seedColor=Color(0xFFEC3013),isDark=dark,style=PaletteStyle.TonalSpot)
    MaterialExpressiveTheme(colorScheme=colors,motionScheme=MotionScheme.expressive(),
        shapes=Shapes(extraSmall=RoundedCornerShape(12.dp),small=RoundedCornerShape(16.dp),medium=RoundedCornerShape(20.dp),large=RoundedCornerShape(28.dp),extraLarge=RoundedCornerShape(32.dp))) {
        CompositionLocalProvider(LocalContext provides scoped, LocalConfiguration provides config,
            LocalLayoutDirection provides rtl, LocalContentColor provides colors.onSurface, content=content)
    }
}
@Composable fun ShieldPanel(modifier:Modifier=Modifier, emphasized:Boolean=false, content:@Composable ColumnScope.()->Unit) {
    Surface(modifier=modifier.fillMaxWidth(),shape=MaterialTheme.shapes.large,
        color=if(emphasized) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainer) {
        Column(Modifier.padding(ShieldSpace.section),verticalArrangement=Arrangement.spacedBy(ShieldSpace.medium),content=content)
    }
}
@Composable fun ShieldButton(label:String,onClick:()->Unit,modifier:Modifier=Modifier,enabled:Boolean=true,primary:Boolean=false) {
    if(primary) Button(onClick=onClick,shapes=ButtonDefaults.shapes(),modifier=modifier.heightIn(min=56.dp),enabled=enabled) { Text(label,style=MaterialTheme.typography.titleMedium) }
    else FilledTonalButton(onClick=onClick,shapes=ButtonDefaults.shapes(),modifier=modifier.heightIn(min=48.dp),enabled=enabled) { Text(label) }
}
@Composable fun ShieldChoices(labels:List<String>,selected:Int,onSelect:(Int)->Unit,enabled:Boolean=true) {
    ButtonGroup(overflowIndicator={ menu -> IconButton(onClick={if(menu.isShowing) menu.dismiss() else menu.show()}) { Icon(Icons.Rounded.MoreVert,contentDescription="More choices") } },
        modifier=Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(ShieldSpace.small)) {
        labels.forEachIndexed { i,label -> toggleableItem(checked=i==selected,onCheckedChange={if(i!=selected) onSelect(i)},label=label,enabled=enabled,weight=1f) }
    }
}
@Composable fun ShieldSetting(title:String,detail:String,checked:Boolean,onChange:(Boolean)->Unit,enabled:Boolean=true) {
    ListItem(colors=ListItemDefaults.colors(containerColor=Color.Transparent),headlineContent={Text(title)},supportingContent={Text(detail)},trailingContent={Switch(checked=checked,onCheckedChange=onChange,enabled=enabled)})
}
@Composable fun ShieldField(value:String,onChange:(String)->Unit,label:String,modifier:Modifier=Modifier,singleLine:Boolean=true) {
    OutlinedTextField(value=value,onValueChange=onChange,label={Text(label)},modifier=modifier.fillMaxWidth(),singleLine=singleLine,
        minLines=if(singleLine) 1 else 5,maxLines=if(singleLine) 1 else 10)
}
@Composable fun ShieldBusy() { LinearWavyProgressIndicator(modifier=Modifier.fillMaxWidth()) }
@Composable fun ShieldConfirm(title:String,text:String,accept:String,cancel:String,onAccept:()->Unit,onDismiss:()->Unit) {
    AlertDialog(onDismissRequest=onDismiss,title={Text(title)},text={Text(text)},confirmButton={TextButton(onClick=onAccept) {Text(accept)}},dismissButton={TextButton(onClick=onDismiss) {Text(cancel)}})
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable fun ShieldShell(page:Int,labels:List<String>,onNavigate:(Int)->Unit,message:String?,clearMessage:()->Unit,content:@Composable ()->Unit) {
    val host=remember { SnackbarHostState() }
    LaunchedEffect(message) { if(message!=null) { host.showSnackbar(message); clearMessage() } }
    val icons=listOf(Icons.Rounded.Security,Icons.Rounded.Dns,Icons.Rounded.Apps,Icons.Rounded.Settings)
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val rail=maxWidth>=840.dp
        Scaffold(topBar={TopAppBar(title={Row(horizontalArrangement=Arrangement.spacedBy(12.dp),verticalAlignment=androidx.compose.ui.Alignment.CenterVertically) {
            Icon(androidx.compose.ui.res.painterResource(com.nourix.routeshield.R.drawable.ic_brand),contentDescription=null,modifier=Modifier.size(32.dp),tint=Color.Unspecified)
            Text("RouteShield",style=MaterialTheme.typography.titleLarge)
        }})},snackbarHost={SnackbarHost(host)},bottomBar={ if(!rail) NavigationBar { labels.forEachIndexed { index,label ->
            NavigationBarItem(selected=index==page,onClick={onNavigate(index)},icon={Icon(icons[index],null)},label={Text(label)})
        } } }) { padding ->
            Row(Modifier.fillMaxSize().padding(padding)) {
                if(rail) NavigationRail { labels.forEachIndexed { index,label -> NavigationRailItem(selected=index==page,onClick={onNavigate(index)},icon={Icon(icons[index],null)},label={Text(label)}) } }
                Box(Modifier.weight(1f).fillMaxHeight(),contentAlignment=androidx.compose.ui.Alignment.TopCenter) {
                    Column(Modifier.widthIn(max=ShieldSpace.reading).fillMaxWidth()) { content() }
                }
            }
        }
    }
}
@Composable fun ShieldCheck(label:String,checked:Boolean,onChange:(Boolean)->Unit,enabled:Boolean=true) {
    ListItem(colors=ListItemDefaults.colors(containerColor=Color.Transparent),headlineContent={Text(label)},leadingContent={Checkbox(checked=checked,onCheckedChange=onChange,enabled=enabled)})
}
@Composable fun ShieldRule() { HorizontalDivider(color=MaterialTheme.colorScheme.outlineVariant) }
@Composable fun ShieldDialog(title:String,onDismiss:()->Unit,secure:Boolean=false,content:@Composable ColumnScope.()->Unit) {
    androidx.compose.ui.window.Dialog(onDismissRequest=onDismiss,properties=androidx.compose.ui.window.DialogProperties(securePolicy=if(secure) androidx.compose.ui.window.SecureFlagPolicy.SecureOn else androidx.compose.ui.window.SecureFlagPolicy.Inherit)) {
        Surface(shape=MaterialTheme.shapes.extraLarge,color=MaterialTheme.colorScheme.surfaceContainerHigh) {
            Column(Modifier.padding(24.dp).heightIn(max=600.dp),verticalArrangement=Arrangement.spacedBy(16.dp)) { Text(title,style=MaterialTheme.typography.headlineSmall); content() }
        }
    }
}
