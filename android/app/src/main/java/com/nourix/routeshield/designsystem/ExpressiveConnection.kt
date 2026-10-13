@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)
package com.nourix.routeshield.designsystem

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.LocalIndication
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Outline
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.graphics.shapes.Morph
import com.nourix.routeshield.R

/** Official Material polygon morph, scaled from its real bounds; spring overshoot is clamped. */
private class ConnectionMorph(private val morph: Morph, private val progress: Float): Shape {
    override fun createOutline(size: Size, layoutDirection: LayoutDirection, density: Density): Outline {
        val bounds=morph.calculateMaxBounds()
        fun x(value:Float)=(value-bounds[0])/(bounds[2]-bounds[0])*size.width
        fun y(value:Float)=(value-bounds[1])/(bounds[3]-bounds[1])*size.height
        val path=Path()
        morph.asCubics(progress.coerceIn(0f,1f)).forEachIndexed { i,cubic ->
            if(i==0) path.moveTo(x(cubic.anchor0X),y(cubic.anchor0Y))
            path.cubicTo(x(cubic.control0X),y(cubic.control0Y),x(cubic.control1X),y(cubic.control1Y),x(cubic.anchor1X),y(cubic.anchor1Y))
        }
        path.close();return Outline.Generic(path)
    }
}

@Composable fun ExpressiveConnectControl(label:String,state:String,running:Boolean,working:Boolean,enabled:Boolean,onClick:()->Unit) {
    val interaction=remember {MutableInteractionSource()}
    val pressed by interaction.collectIsPressedAsState()
    val morph=remember {Morph(MaterialShapes.Cookie9Sided,MaterialShapes.Puffy)}
    val progress by animateFloatAsState(if(pressed) 0.45f else if(running) 1f else 0f,MaterialTheme.motionScheme.fastSpatialSpec(),label="connection-morph")
    val color by animateColorAsState(if(running) MaterialTheme.colorScheme.tertiaryContainer else MaterialTheme.colorScheme.primary,MaterialTheme.motionScheme.fastEffectsSpec(),label="connection-color")
    val foreground=if(running) MaterialTheme.colorScheme.onTertiaryContainer else MaterialTheme.colorScheme.onPrimary
    Column(Modifier.widthIn(min=144.dp,max=224.dp).clickable(interactionSource=interaction,indication=LocalIndication.current,enabled=enabled,role=Role.Button,onClick=onClick)
        .semantics {stateDescription=state}.padding(8.dp),horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.spacedBy(12.dp)) {
        Surface(color=if(enabled) color else MaterialTheme.colorScheme.surfaceContainerHighest,contentColor=if(enabled) foreground else MaterialTheme.colorScheme.onSurfaceVariant,
            shape=ConnectionMorph(morph,progress),modifier=Modifier.size(if(LocalConfiguration.current.fontScale>=1.5f) 144.dp else 160.dp)) {
            Box(contentAlignment=Alignment.Center) {
                if(working) LoadingIndicator(modifier=Modifier.size(64.dp),color=foreground)
                else Icon(Icons.Rounded.PowerSettingsNew,null,Modifier.size(60.dp))
            }
        }
        Text(label,style=MaterialTheme.typography.titleMedium,fontWeight=FontWeight.Bold)
    }
}

/** Only real successful HTTPS measurements produce bars. Failed rounds are explicit gaps. */
@Composable fun ExpressiveQuality(history:List<Long?>,latency:Long?) {
    val format=java.text.NumberFormat.getIntegerInstance(LocalConfiguration.current.locales[0])
    val failed=stringResource(R.string.probe_failed)
    val description=stringResource(R.string.quality_history_accessibility,history.joinToString(", ") {if(it==null) failed else format.format(it)})
    Surface(shape=RoundedCornerShape(topStart=32.dp,topEnd=32.dp,bottomStart=8.dp,bottomEnd=32.dp),color=MaterialTheme.colorScheme.secondaryContainer,
        contentColor=MaterialTheme.colorScheme.onSecondaryContainer,modifier=Modifier.fillMaxWidth()) {
        Column(Modifier.padding(20.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
            Text(stringResource(R.string.response_time),style=MaterialTheme.typography.labelLarge)
            Row(verticalAlignment=Alignment.Bottom,horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                Text(latency?.let {format.format(it)} ?: "—",style=MaterialTheme.typography.displaySmall,fontWeight=FontWeight.Bold)
                Text(stringResource(R.string.milliseconds),Modifier.padding(bottom=6.dp),style=MaterialTheme.typography.titleMedium)
            }
            if(history.isNotEmpty()) {
                val max=history.filterNotNull().maxOrNull()?.coerceAtLeast(1L) ?: 1L
                Row(Modifier.fillMaxWidth().height(96.dp).semantics {contentDescription=description},verticalAlignment=Alignment.Bottom,horizontalArrangement=Arrangement.spacedBy(4.dp)) {
                    history.forEach {sample ->
                        val height=if(sample==null) 8.dp else (16f+sample.toFloat()/max*80f).dp
                        Surface(modifier=Modifier.weight(1f).height(height),shape=RoundedCornerShape(50),
                            color=if(sample==null) MaterialTheme.colorScheme.outline else if(sample==history.filterNotNull().minOrNull()) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.tertiary) {}
                    }
                }
                Text(stringResource(R.string.quality_chart_detail),style=MaterialTheme.typography.bodySmall)
            } else Text(stringResource(R.string.quality_empty),style=MaterialTheme.typography.bodySmall)
        }
    }
}
