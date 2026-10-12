package com.nourix.routeshield

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Device-bound AES-GCM. No plaintext profiles, backup, telemetry, Supabase or cloud sync. */
class ProfileStore(private val context: Context) {
    private val file = AtomicFile(File(context.filesDir, "profiles.enc"))
    private val alias = "RouteShield.ProfileKey.v1"
    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(alias, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
        }.generateKey()
    }
    @Synchronized fun load(): JSONObject {
        if (!file.baseFile.exists()) return JSONObject().put("nodes", JSONArray()).put("selected", 0).put("mode", "all").put("apps", JSONArray())
        val raw = file.readFully()
        require(raw.size in 28..ProfileConfig.MAX_BYTES + 1024) { "Stored profile size is invalid" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, raw.copyOfRange(0, 12)))
        cipher.updateAAD("RouteShield.v1".toByteArray())
        return JSONObject(String(cipher.doFinal(raw.copyOfRange(12, raw.size)), Charsets.UTF_8))
    }
    @Synchronized fun save(value: JSONObject) {
        val plain = value.toString().toByteArray()
        require(plain.size <= ProfileConfig.MAX_BYTES) { "Profiles are larger than 5 MB" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE, key())
        cipher.updateAAD("RouteShield.v1".toByteArray())
        val bytes = cipher.iv + cipher.doFinal(plain)
        val output = file.startWrite()
        try { output.write(bytes); file.finishWrite(output) } catch (e: Exception) { file.failWrite(output); throw e }
    }
    @Synchronized fun clear() { file.delete() }
}
