package com.nourix.routeshield

import java.io.InputStream
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

/** Bound actual bytes, not provider metadata. Decode strictly; never retain URI permissions. */
object ProfileDocument {
    fun read(stream:InputStream):String {
        val bytes=ByteArrayOutputStream();val buffer=ByteArray(8192)
        while(true) {
            val count=stream.read(buffer);if(count<0) break
            require(bytes.size()+count<=ProfileConfig.MAX_BYTES) {"Profile file too large"}
            bytes.write(buffer,0,count)
        }
        return Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes.toByteArray())).toString()
            .also {require(it.isNotBlank() && '\u0000' !in it) {"Empty or binary profile file"}}
    }
}
