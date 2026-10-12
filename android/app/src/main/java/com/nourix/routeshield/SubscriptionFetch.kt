package com.nourix.routeshield

import org.json.JSONObject
import java.net.URI
import java.net.URL
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import javax.net.ssl.HttpsURLConnection

/** HTTPS-only, bounded redirects and bytes; an overall deadline also disconnects a stalled body. */
object SubscriptionFetch {
    data class Result(val body: String, val usage: JSONObject)
    private val timer = Executors.newSingleThreadScheduledExecutor()
    fun fetch(value: String): Result {
        var uri = URI(value.trim())
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(35)
        repeat(6) { hop ->
            require(uri.scheme == "https" && uri.userInfo == null) { "Subscription must use HTTPS without userinfo" }
            val remaining = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime()).toInt()
            check(remaining > 0) { "Subscription timed out" }
            val connection = uri.toURL().openConnection() as HttpsURLConnection
            connection.instanceFollowRedirects = false; connection.useCaches = false
            connection.connectTimeout = remaining.coerceAtMost(10000); connection.readTimeout = remaining.coerceAtMost(10000)
            connection.setRequestProperty("User-Agent", "RouteShield-Android/0.1 (sing-box)")
            val close = timer.schedule({ connection.disconnect() }, remaining.toLong(), TimeUnit.MILLISECONDS)
            try {
                val status = connection.responseCode
                if (status in setOf(301,302,303,307,308)) {
                    check(hop < 5) { "Too many redirects" }
                    uri = uri.resolve(connection.getHeaderField("Location") ?: error("Missing redirect"))
                    return@repeat
                }
                check(status in 200..299) { "Subscription request failed" }
                check(connection.contentLengthLong <= ProfileConfig.MAX_BYTES) { "Subscription too large" }
                val bytes = java.io.ByteArrayOutputStream()
                connection.inputStream.use { stream ->
                    val buffer = ByteArray(32768)
                    while (true) {
                        check(System.nanoTime() < deadline) { "Subscription timed out" }
                        val count = stream.read(buffer); if (count < 0) break
                        check(bytes.size() + count <= ProfileConfig.MAX_BYTES) { "Subscription too large" }
                        bytes.write(buffer,0,count)
                    }
                }
                val usage = SubscriptionMetadata.parse(connection.getHeaderField("subscription-userinfo"))
                return Result(bytes.toString("UTF-8"),usage)
            } finally { close.cancel(false); connection.disconnect() }
        }
        error("Subscription redirect chain exhausted")
    }
}
