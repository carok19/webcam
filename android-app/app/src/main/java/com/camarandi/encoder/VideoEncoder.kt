package com.camarandi.encoder

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.view.Surface

/**
 * Codificador por hardware con entrada por Surface (la cámara dibuja directo en él, sin copias).
 * Ajustado para baja latencia: sin B-frames, prioridad tiempo real, KEY_LATENCY=1,
 * keyframe cada 1 s, CBR. La salida se entrega en el mismo hilo que la saca del codificador:
 * no hay colas intermedias.
 */
class VideoEncoder(
    private val config: Config,
    private val sink: Sink,
) {
    data class Config(
        val mime: String,
        val codecName: String,
        val width: Int,
        val height: Int,
        val fps: Int,
        val bitrateBps: Int,
        val cbr: Boolean,
        val iFrameIntervalSec: Int = 1,
    )

    interface Sink {
        /** [data] contiene el frame completo en Annex-B; los keyframes ya llevan VPS/SPS/PPS delante. */
        fun onEncodedFrame(data: ByteArray, length: Int, ptsUs: Long, keyframe: Boolean)
        fun onEncoderError(e: Exception)
    }

    companion object { private const val TAG = "Codificador" }

    private var codec: MediaCodec? = null
    var inputSurface: Surface? = null
        private set
    @Volatile private var running = false
    private var thread: Thread? = null
    private var csd: ByteArray? = null
    private var outBuf = ByteArray(512 * 1024)
    private var lastSyncRequestMs = 0L

    @Volatile var currentBitrate = config.bitrateBps
        private set
    /** Promedio móvil del tiempo que tarda el codificador (ms). */
    @Volatile var avgEncodeMs = 0.0
        private set

    val isHevc get() = config.mime == MediaFormat.MIMETYPE_VIDEO_HEVC

    fun start(): Surface {
        val c = MediaCodec.createByCodecName(config.codecName)
        try {
            c.configure(buildFormat(full = true), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        } catch (e: Exception) {
            // Algunos codificadores rechazan claves opcionales: reintentar con lo mínimo.
            Log.w(TAG, "configure completo falló, reintentando básico", e)
            c.reset()
            c.configure(buildFormat(full = false), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        }
        val s = c.createInputSurface()
        c.start()
        codec = c
        inputSurface = s
        running = true
        thread = Thread({ drainLoop() }, "codificador").apply { priority = Thread.MAX_PRIORITY; start() }
        return s
    }

    private fun buildFormat(full: Boolean): MediaFormat {
        val f = MediaFormat.createVideoFormat(config.mime, config.width, config.height)
        f.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
        f.setInteger(MediaFormat.KEY_BIT_RATE, config.bitrateBps)
        f.setInteger(MediaFormat.KEY_FRAME_RATE, config.fps)
        f.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, config.iFrameIntervalSec)
        f.setInteger(
            MediaFormat.KEY_BITRATE_MODE,
            if (config.cbr) MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR
            else MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR,
        )
        if (!full) return f
        f.setInteger(MediaFormat.KEY_PRIORITY, 0) // 0 = tiempo real
        f.setInteger(MediaFormat.KEY_OPERATING_RATE, config.fps)
        f.setInteger(MediaFormat.KEY_LATENCY, 1)
        if (Build.VERSION.SDK_INT >= 29) {
            f.setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            f.setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1)
        }
        if (isHevc) {
            f.setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.HEVCProfileMain)
        } else if (Build.VERSION.SDK_INT >= 29 && EncoderCatalog.supportsProfile(config.codecName, config.mime, MediaCodecInfo.CodecProfileLevel.AVCProfileHigh)) {
            // High sin B-frames: mejor calidad por bit que Baseline, misma latencia.
            f.setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileHigh)
        } else {
            f.setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline)
        }
        // Claves de baja latencia de fabricantes (se ignoran si no existen).
        if (config.codecName.contains("qcom", true) || config.codecName.contains("qti", true)) {
            f.setInteger("vendor.qti-ext-enc-low-latency.enable", 1)
        }
        return f
    }

    private fun drainLoop() {
        val info = MediaCodec.BufferInfo()
        while (running) {
            val c = codec ?: break
            val idx = try {
                c.dequeueOutputBuffer(info, 10_000)
            } catch (e: Exception) {
                if (running) sink.onEncoderError(e)
                break
            }
            if (idx < 0) continue
            try {
                val buf = c.getOutputBuffer(idx)
                if (buf != null && info.size > 0) {
                    buf.position(info.offset)
                    buf.limit(info.offset + info.size)
                    if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                        csd = ByteArray(info.size).also { buf.get(it) }
                    } else {
                        val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                        val prefix = if (key && !startsWithParams(buf, info.offset)) csd else null
                        val total = info.size + (prefix?.size ?: 0)
                        if (outBuf.size < total) outBuf = ByteArray(total + total / 2)
                        var off = 0
                        if (prefix != null) { System.arraycopy(prefix, 0, outBuf, 0, prefix.size); off = prefix.size }
                        buf.get(outBuf, off, info.size)
                        measureLatency(info.presentationTimeUs)
                        sink.onEncodedFrame(outBuf, total, info.presentationTimeUs, key)
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "error procesando salida", e)
            } finally {
                runCatching { c.releaseOutputBuffer(idx, false) }
            }
            if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
        }
    }

    /** ¿El keyframe ya trae VPS/SPS delante (KEY_PREPEND_HEADER_TO_SYNC_FRAMES)? */
    private fun startsWithParams(b: java.nio.ByteBuffer, off: Int): Boolean {
        var i = off
        val end = minOf(b.limit(), off + 8)
        while (i + 3 < end) {
            if (b.get(i).toInt() == 0 && b.get(i + 1).toInt() == 0 && b.get(i + 2).toInt() == 1) {
                val h = b.get(i + 3).toInt() and 0xFF
                return if (isHevc) ((h shr 1) and 0x3F) == 32 else (h and 0x1F) == 7
            }
            i++
        }
        return false
    }

    /** Clock base de los timestamps de la cámara (REALTIME o MONOTONIC). */
    @Volatile var timestampRealtime = true

    private fun measureLatency(ptsUs: Long) {
        val nowUs = if (timestampRealtime) SystemClock.elapsedRealtimeNanos() / 1000 else System.nanoTime() / 1000
        val ms = (nowUs - ptsUs) / 1000.0
        if (ms in 0.0..1500.0) avgEncodeMs = if (avgEncodeMs == 0.0) ms else avgEncodeMs * 0.9 + ms * 0.1
    }

    /** Edad (ms) de un frame según su pts; -1 si no se puede saber. */
    fun ageMs(ptsUs: Long): Double {
        val nowUs = if (timestampRealtime) SystemClock.elapsedRealtimeNanos() / 1000 else System.nanoTime() / 1000
        val ms = (nowUs - ptsUs) / 1000.0
        return if (ms in 0.0..10_000.0) ms else -1.0
    }

    fun setBitrate(bps: Int) {
        val c = codec ?: return
        if (bps == currentBitrate) return
        runCatching {
            c.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, bps) })
            currentBitrate = bps
        }
    }

    /** Pide un keyframe inmediato (como máximo uno cada 200 ms). */
    fun requestKeyframe() {
        val c = codec ?: return
        val now = SystemClock.elapsedRealtime()
        if (now - lastSyncRequestMs < 200) return
        lastSyncRequestMs = now
        runCatching { c.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }) }
    }

    fun stop() {
        running = false
        runCatching { codec?.signalEndOfInputStream() }
        thread?.join(500)
        runCatching { codec?.stop() }
        runCatching { codec?.release() }
        runCatching { inputSurface?.release() }
        codec = null
        inputSurface = null
    }
}
