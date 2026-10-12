package com.nourix.routeshield

import android.content.Intent
import android.net.VpnService
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.lifecycle.ViewModelProvider

class MainActivity:ComponentActivity() {
    private lateinit var model:ShieldModel
    private val consent=registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if(result.resultCode==RESULT_OK) startVpn() else TunnelStatus.update("permission")
    }
    private val document=registerForActivityResult(ActivityResultContracts.CreateDocument("text/plain")) { uri ->
        if(uri!=null) lifecycleExport(uri)
    }
    override fun onCreate(savedInstanceState:Bundle?) {
        super.onCreate(savedInstanceState); enableEdgeToEdge(); model=ViewModelProvider(this)[ShieldModel::class.java]
        setContent { ShieldApp(model,onConnect={ toggle() },onSystemVpn={ startActivity(Intent(Settings.ACTION_VPN_SETTINGS)) },onExport={document.launch("RouteShield-diagnostics.txt")}) }
    }
    override fun onResume() { super.onResume(); TunnelService.refreshProtection() }
    private fun toggle() {
        if(model.tunnel.running) {
            if(model.tunnel.stage=="stopping") return
            model.event("Requested VPN shutdown"); TunnelStatus.update("stopping")
            if(!TunnelService.requestStop()) {
                // Only recover an orphaned UI state when no service instance exists.
                stopService(Intent(this,TunnelService::class.java)); TunnelStatus.update("idle")
            }
            return
        }
        if(model.saved.optString("mode")=="include" && (model.saved.optJSONArray("apps")?.length() ?: 0)==0) { model.message=getString(R.string.select_apps); return }
        val intent=VpnService.prepare(this); if(intent==null) startVpn() else consent.launch(intent)
    }
    private fun startVpn() {
        if(android.os.Build.VERSION.SDK_INT>=33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS),10)
        model.event("Requested VPN startup"); startForegroundService(Intent(this,TunnelService::class.java))
    }
    private fun lifecycleExport(uri:android.net.Uri) {
        val text=model.diagnostics()
        Thread { runCatching { contentResolver.openOutputStream(uri)?.use {it.write(text.toByteArray())} } }.start()
    }
}
