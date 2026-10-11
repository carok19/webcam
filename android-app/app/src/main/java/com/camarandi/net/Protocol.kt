package com.camarandi.net

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Protocolo CNDI v1 (ver docs/PROTOCOLO.md). Todo little-endian.
 * Debe coincidir byte a byte con windows-receiver/src/CamaraNdi.Core/Protocol.cs.
 */
object Protocol {
    const val MAGIC: Int = 0x4E43 // bytes 'C','N'
    const val VERSION: Int = 1
    const val HEADER_SIZE = 8
    const val VIDEO_HEADER_SIZE = 32
    const val MAX_PAYLOAD = 1168
    const val MAX_PACKET = VIDEO_HEADER_SIZE + MAX_PAYLOAD
    const val MEDIA_PORT = 47810
    const val DISCOVERY_PORT = 47811

    const val HELLO: Int = 0x01
    const val PING: Int = 0x02
    const val BYE: Int = 0x03
    const val DISCOVER: Int = 0x04
    const val VIDEO: Int = 0x10

    const val HELLO_ACK: Int = 0x81
    const val PONG: Int = 0x82
    const val NACK: Int = 0x83
    const val KEYFRAME_REQ: Int = 0x84
    const val REPORT: Int = 0x85
    const val DISCONNECT: Int = 0x86
    const val DISCOVER_REPLY: Int = 0x87

    const val FLAG_KEYFRAME = 0x01
    const val FLAG_RETRANSMIT = 0x02

    const val CODEC_H264 = 1
    const val CODEC_HEVC = 2

    fun writeHeader(b: ByteBuffer, type: Int, sessionId: Int) {
        b.order(ByteOrder.LITTLE_ENDIAN)
        b.putShort(MAGIC.toShort())
        b.put(VERSION.toByte())
        b.put(type.toByte())
        b.putInt(sessionId)
    }

    /** Devuelve el tipo de mensaje o -1 si no es un paquete válido. */
    fun readType(data: ByteArray, len: Int): Int {
        if (len < HEADER_SIZE) return -1
        val magic = (data[0].toInt() and 0xFF) or ((data[1].toInt() and 0xFF) shl 8)
        if (magic != MAGIC || (data[2].toInt() and 0xFF) != VERSION) return -1
        return data[3].toInt() and 0xFF
    }

    fun buildJson(type: Int, sessionId: Int, json: String): ByteArray {
        val body = json.toByteArray(Charsets.UTF_8)
        val b = ByteBuffer.allocate(HEADER_SIZE + body.size)
        writeHeader(b, type, sessionId)
        b.put(body)
        return b.array()
    }

    fun jsonBody(data: ByteArray, len: Int): String =
        String(data, HEADER_SIZE, len - HEADER_SIZE, Charsets.UTF_8)

    fun buildPing(sessionId: Int, pingId: Int, timeUs: Long): ByteArray {
        val b = ByteBuffer.allocate(HEADER_SIZE + 12)
        writeHeader(b, PING, sessionId)
        b.putInt(pingId)
        b.putLong(timeUs)
        return b.array()
    }

    fun buildBye(sessionId: Int): ByteArray {
        val b = ByteBuffer.allocate(HEADER_SIZE)
        writeHeader(b, BYE, sessionId)
        return b.array()
    }

    /** Escribe la cabecera de video en [dst] (ya del tamaño correcto). */
    fun writeVideoHeader(
        dst: ByteArray, sessionId: Int, seq: Int, frameId: Int, fragIdx: Int, fragCount: Int,
        flags: Int, codec: Int, ptsUs: Long,
    ) {
        val b = ByteBuffer.wrap(dst, 0, VIDEO_HEADER_SIZE)
        writeHeader(b, VIDEO, sessionId)
        b.putInt(seq)
        b.putInt(frameId)
        b.putShort(fragIdx.toShort())
        b.putShort(fragCount.toShort())
        b.put(flags.toByte())
        b.put(codec.toByte())
        b.putShort(0)
        b.putLong(ptsUs)
    }

    fun le(data: ByteArray): ByteBuffer = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)

    data class Report(
        val intervalMs: Int,
        val expected: Long,
        val missing: Long,
        val lost: Long,
        val framesOutput: Long,
        val framesDropped: Long,
        val jitterMs: Double,
        val receiverLatencyMs: Double,
        val suggestedKbps: Long,
        val rxKbps: Long,
    )

    fun readReport(data: ByteArray, len: Int): Report? {
        if (len < HEADER_SIZE + 36) return null
        val b = le(data)
        b.position(8)
        val interval = b.int
        val expected = b.int.toLong() and 0xFFFFFFFFL
        val missing = b.int.toLong() and 0xFFFFFFFFL
        val lost = b.int.toLong() and 0xFFFFFFFFL
        val out = b.int.toLong() and 0xFFFFFFFFL
        val dropped = b.int.toLong() and 0xFFFFFFFFL
        val jitter = (b.short.toInt() and 0xFFFF) / 10.0
        val lat = (b.short.toInt() and 0xFFFF) / 10.0
        val sugg = b.int.toLong() and 0xFFFFFFFFL
        val rx = b.int.toLong() and 0xFFFFFFFFL
        return Report(interval, expected, missing, lost, out, dropped, jitter, lat, sugg, rx)
    }

    fun readNack(data: ByteArray, len: Int): IntArray {
        if (len < HEADER_SIZE + 2) return IntArray(0)
        val b = le(data)
        b.position(8)
        var n = b.short.toInt() and 0xFFFF
        n = minOf(n, (len - 10) / 4)
        return IntArray(n) { b.int }
    }
}
