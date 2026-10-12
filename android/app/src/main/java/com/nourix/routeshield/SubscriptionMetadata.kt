package com.nourix.routeshield

import org.json.JSONObject

/** Provider-reported metadata is untrusted, not a live traffic counter. */
object SubscriptionMetadata {
    const val MAX_EXPIRY = 253402300799L // last second in year 9999
    fun parse(header: String?): JSONObject = JSONObject().apply {
        header?.split(';')?.forEach { token ->
            val parts=token.trim().split('=',limit=2)
            if(parts.size==2 && parts[0] in setOf("upload","download","total","expire")) {
                parts[1].toLongOrNull()?.takeIf { it>=0 && (parts[0]!="expire" || it<=MAX_EXPIRY) }?.let { put(parts[0],it) }
            }
        }
    }
    fun remaining(usage: JSONObject): Long? {
        val total=usage.optLong("total"); if(total<=0) return null
        val afterUpload=total-usage.optLong("upload").coerceIn(0,total)
        return afterUpload-usage.optLong("download").coerceIn(0,afterUpload)
    }
}
