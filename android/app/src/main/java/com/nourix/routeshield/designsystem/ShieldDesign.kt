@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)
package com.nourix.routeshield.designsystem

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.LocalIndication
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.Alignment
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.selection.selectable
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
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.unit.*
import com.materialkolor.PaletteStyle
import com.materialkolor.rememberDynamicColorScheme
import java.util.Locale

/** New RouteShield vocabulary; informed by Sekke's layering, not copied from its private source. */
object ShieldSpace { val small=8.dp; val medium=16.dp; val gutter=16.dp; val section=24.dp; val reading=680.dp }
@Composable fun ShieldTheme(mode:String="system", language:String="system", seedKey:String="brand", content:@Composable ()->Unit) {
    val dark=when(mode) { "dark"->true; "light"->false; else->isSystemInDarkTheme() }
    val context=LocalContext.current
    val config=android.content.res.Configuration(LocalConfiguration.current)
    if(language!="system") config.setLocale(Locale.forLanguageTag(language))
    // Keep the Activity in the wrapper chain for platform windows and lifecycle owners.
    val scoped=remember(context,language,config) {
        android.view.ContextThemeWrapper(context,context.theme).apply {applyOverrideConfiguration(config)}
    }
    val rtl=if(config.locales[0].language in listOf("fa","ar")) LayoutDirection.Rtl else LayoutDirection.Ltr
    val colors=rememberDynamicColorScheme(seedColor=Color(com.nourix.routeshield.ThemePalette.color(seedKey)),isDark=dark,style=PaletteStyle.Vibrant)
    val base=Typography()
    fun TextStyle.localized()=if(rtl==LayoutDirection.Rtl) copy(letterSpacing=0.sp,lineHeight=(fontSize.value*1.55f).sp,textDirection=TextDirection.Content) else copy(textDirection=TextDirection.Content)
    val typography=base.copy(headlineLarge=base.headlineLarge.localized(),headlineMedium=base.headlineMedium.localized(),headlineSmall=base.headlineSmall.localized(),
        titleLarge=base.titleLarge.localized(),titleMedium=base.titleMedium.localized(),titleSmall=base.titleSmall.localized(),
        bodyLarge=base.bodyLarge.localized(),bodyMedium=base.bodyMedium.localized(),bodySmall=base.bodySmall.localized(),
        labelLarge=base.labelLarge.localized(),labelMedium=base.labelMedium.localized(),labelSmall=base.labelSmall.localized())
    MaterialExpressiveTheme(colorScheme=colors,motionScheme=MotionScheme.expressive(),typography=typography,
        shapes=Shapes(extraSmall=RoundedCornerShape(4.dp),small=RoundedCornerShape(8.dp),medium=RoundedCornerShape(12.dp),large=RoundedCornerShape(16.dp),extraLarge=RoundedCornerShape(28.dp))) {
        CompositionLocalProvider(LocalContext provides scoped, LocalConfiguration provides config,
            LocalLayoutDirection provides rtl, LocalContentColor provides colors.onSurface, content=content)
    }
}
@Composable fun ShieldPanel(modifier:Modifier=Modifier, emphasized:Boolean=false, compact:Boolean=false, content:@Composable ColumnScope.()->Unit) {
    Surface(modifier=modifier.fillMaxWidth(),shape=if(emphasized) RoundedCornerShape(32.dp) else RoundedCornerShape(24.dp),
        color=if(emphasized) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerLow) {
        Column(Modifier.padding(if(emphasized) ShieldSpace.section else ShieldSpace.medium),verticalArrangement=Arrangement.spacedBy(if(compact) ShieldSpace.small else ShieldSpace.medium),content=content)
    }
}
@Composable fun ShieldButton(label:String,onClick:()->Unit,modifier:Modifier=Modifier,enabled:Boolean=true,primary:Boolean=false) {
    if(primary) Button(onClick=onClick,shapes=ButtonDefaults.shapes(),modifier=modifier.heightIn(min=56.dp),enabled=enabled) { Text(label,style=MaterialTheme.typography.titleMedium) }
    else FilledTonalButton(onClick=onClick,shapes=ButtonDefaults.shapes(),modifier=modifier.heightIn(min=48.dp),enabled=enabled) { Text(label) }
}
@Composable fun ShieldChoices(labels:List<String>,selected:Int,onSelect:(Int)->Unit,enabled:Boolean=true) {
    if(LocalConfiguration.current.fontScale>=1.5f) Column(verticalArrangement=Arrangement.spacedBy(4.dp)) {
        labels.forEachIndexed { i,label -> ToggleButton(checked=i==selected,onCheckedChange={if(it) onSelect(i)},enabled=enabled,
            modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).semantics {role=Role.RadioButton}) {Text(label)} }
    } else Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(ButtonGroupDefaults.ConnectedSpaceBetween)) {
        labels.forEachIndexed { i,label -> ToggleButton(checked=i==selected,onCheckedChange={if(it) onSelect(i)},enabled=enabled,
            shapes=when(i) {0->ButtonGroupDefaults.connectedLeadingButtonShapes();labels.lastIndex->ButtonGroupDefaults.connectedTrailingButtonShapes();else->ButtonGroupDefaults.connectedMiddleButtonShapes()},
            modifier=Modifier.weight(1f).heightIn(min=48.dp).semantics {role=Role.RadioButton}) {Text(label)} }
    }
}
@Composable fun ShieldSetting(title:String,detail:String,checked:Boolean,onChange:(Boolean)->Unit,enabled:Boolean=true) {
    Column(Modifier.fillMaxWidth().padding(vertical=4.dp),verticalArrangement=Arrangement.spacedBy(4.dp)) {
        if(LocalConfiguration.current.fontScale>=1.5f) {
            Text(title,style=MaterialTheme.typography.titleMedium)
            Switch(checked=checked,onCheckedChange=onChange,enabled=enabled,modifier=Modifier.semantics {contentDescription=title})
        } else Row(Modifier.fillMaxWidth(),verticalAlignment=Alignment.CenterVertically,horizontalArrangement=Arrangement.spacedBy(12.dp)) {
            Text(title,Modifier.weight(1f),style=MaterialTheme.typography.titleMedium)
            Switch(checked=checked,onCheckedChange=onChange,enabled=enabled,modifier=Modifier.semantics {contentDescription=title})
        }
        Text(detail,style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
    }
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
        val scrollBehavior=TopAppBarDefaults.exitUntilCollapsedScrollBehavior()
        LaunchedEffect(page) {scrollBehavior.state.heightOffset=0f;scrollBehavior.state.contentOffset=0f}
        Scaffold(modifier=Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
            topBar={MediumFlexibleTopAppBar(
                title={Text(labels[page],fontWeight=FontWeight.Bold)},
                subtitle={Text("RouteShield",style=MaterialTheme.typography.labelLarge)},
                actions={Icon(androidx.compose.ui.res.painterResource(com.nourix.routeshield.R.drawable.ic_brand),contentDescription=null,modifier=Modifier.padding(end=16.dp).size(28.dp),tint=Color.Unspecified)},
                scrollBehavior=scrollBehavior)},snackbarHost={SnackbarHost(host)},bottomBar={ if(!rail) NavigationBar { labels.forEachIndexed { index,label ->
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
@OptIn(ExperimentalMaterial3Api::class)
@Composable fun ShieldDialog(title:String,onDismiss:()->Unit,secure:Boolean=false,dismissible:Boolean=true,content:@Composable ColumnScope.()->Unit) {
    val canDismiss by rememberUpdatedState(dismissible)
    val state=rememberModalBottomSheetState(skipPartiallyExpanded=true,confirmValueChange={it!=SheetValue.Hidden || canDismiss})
    ModalBottomSheet(onDismissRequest=onDismiss,sheetState=state,
        properties=ModalBottomSheetProperties(securePolicy=if(secure) androidx.compose.ui.window.SecureFlagPolicy.SecureOn else androidx.compose.ui.window.SecureFlagPolicy.Inherit,
            shouldDismissOnBackPress=dismissible,shouldDismissOnClickOutside=dismissible),
        containerColor=MaterialTheme.colorScheme.surfaceContainerHigh) {
        Column(Modifier.fillMaxWidth().heightIn(max=600.dp).padding(horizontal=24.dp).padding(bottom=24.dp)
            .testTag("profile-import-sheet"),verticalArrangement=Arrangement.spacedBy(16.dp)) {
            Text(title,style=MaterialTheme.typography.headlineSmall);content()
        }
    }
}

@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable fun ShieldColors(selected:String,labels:List<String>,enabled:Boolean,onSelect:(String)->Unit) {
    FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp),verticalArrangement=Arrangement.spacedBy(4.dp)) {
        com.nourix.routeshield.ThemePalette.seeds.entries.forEachIndexed { index,entry ->
            FilterChip(selected=selected==entry.key,onClick={onSelect(entry.key)},enabled=enabled,label={Text(labels[index])},leadingIcon={
                Surface(color=Color(entry.value),shape=RoundedCornerShape(50),modifier=Modifier.size(18.dp)) {}
            })
        }
    }
}
@Composable fun ShieldGroupTabs(names:List<String>,selected:Int,enabled:Boolean,onSelect:(Int)->Unit) {
    Row(Modifier.fillMaxWidth().horizontalScroll(androidx.compose.foundation.rememberScrollState()),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        names.forEachIndexed { index,name -> FilterChip(selected=selected==index,onClick={onSelect(index)},enabled=enabled,
            label={Text(name,modifier=Modifier.widthIn(max=180.dp),maxLines=1,overflow=androidx.compose.ui.text.style.TextOverflow.Ellipsis)}) }
    }
}
@Composable fun ShieldNodeRow(title:String,subtitle:String,trailing:String?,selected:Boolean,enabled:Boolean,onSelect:()->Unit) {
    val interaction=remember {MutableInteractionSource()}
    val pressed by interaction.collectIsPressedAsState()
    val radius by animateDpAsState(if(selected) 20.dp else if(pressed) 16.dp else 8.dp,MaterialTheme.motionScheme.fastSpatialSpec(),label="route-shape")
    val color by animateColorAsState(if(selected) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceContainer,MaterialTheme.motionScheme.fastEffectsSpec(),label="route-color")
    Surface(color=color,contentColor=if(selected) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurface,
        shape=RoundedCornerShape(radius.coerceAtLeast(0.dp)),
        modifier=Modifier.fillMaxWidth().selectable(selected=selected,enabled=enabled,role=Role.RadioButton,interactionSource=interaction,indication=LocalIndication.current,onClick=onSelect)) {
        Row(Modifier.padding(horizontal=12.dp,vertical=12.dp),verticalAlignment=Alignment.CenterVertically,horizontalArrangement=Arrangement.spacedBy(12.dp)) {
            RadioButton(selected=selected,onClick=null,enabled=enabled)
            Column(Modifier.weight(1f),verticalArrangement=Arrangement.spacedBy(4.dp)) {
                Text(title,style=MaterialTheme.typography.titleMedium.copy(fontWeight=if(selected) FontWeight.Bold else FontWeight.Medium))
                Text(subtitle,style=MaterialTheme.typography.bodySmall)
                if(trailing!=null) Text(trailing,style=MaterialTheme.typography.labelLarge,color=if(selected) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}
@Composable fun ShieldAppSwitch(label:String,pkg:String,detail:String,on:Boolean,enabled:Boolean,onChange:(Boolean)->Unit) {
    ListItem(colors=ListItemDefaults.colors(containerColor=Color.Transparent),headlineContent={Text(label,maxLines=1,overflow=androidx.compose.ui.text.style.TextOverflow.Ellipsis)},
        supportingContent={Column {Text(pkg,style=MaterialTheme.typography.bodySmall);Text(detail,style=MaterialTheme.typography.labelSmall)}},
        trailingContent={Switch(checked=on,onCheckedChange=onChange,enabled=enabled,modifier=Modifier.semantics {contentDescription=label})})
}

@Composable fun ShieldTextAction(label:String,onClick:()->Unit,enabled:Boolean=true,modifier:Modifier=Modifier,destructive:Boolean=false) {
    TextButton(onClick=onClick,enabled=enabled,modifier=modifier.heightIn(min=48.dp),colors=ButtonDefaults.textButtonColors(contentColor=if(destructive) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary)) {Text(label,style=MaterialTheme.typography.labelLarge)}
}
