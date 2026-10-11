package com.camarandi.encoder

import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build

/** Qué codificadores por hardware tiene el celular y qué pueden sostener de verdad. */
object EncoderCatalog {

    data class Encoder(
        val name: String,
        val mime: String,
        val info: MediaCodecInfo,
        val caps: MediaCodecInfo.CodecCapabilities,
    ) {
        val isHevc get() = mime == MediaFormat.MIMETYPE_VIDEO_HEVC
        val supportsCbr: Boolean
            get() = caps.encoderCapabilities?.isBitrateModeSupported(MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR) == true
        val maxBitrate: Int get() = caps.videoCapabilities?.bitrateRange?.upper ?: 50_000_000

        /**
         * true si el codificador declara que sostiene ese tamaño y fps.
         * Usa los "performance points" (Android 10+) o las tasas medidas por el fabricante.
         */
        fun sustains(w: Int, h: Int, fps: Int): Pair<Boolean, String> {
            val vc = caps.videoCapabilities ?: return false to "sin capacidades de video"
            if (!vc.isSizeSupported(w, h)) return false to "el codificador no acepta ${w}x$h"
            if (!vc.areSizeAndRateSupported(w, h, fps.toDouble())) return false to "el codificador no llega a ${fps} fps en ${w}x$h"
            if (Build.VERSION.SDK_INT >= 29) {
                val pps = vc.supportedPerformancePoints
                if (!pps.isNullOrEmpty()) {
                    val ok = pps.any { it.covers(MediaCodecInfo.VideoCapabilities.PerformancePoint(w, h, fps)) }
                    return if (ok) true to "" else false to "rendimiento insuficiente para ${h}p$fps"
                }
            }
            val achievable = runCatching { vc.getAchievableFrameRatesFor(w, h) }.getOrNull()
            if (achievable != null && achievable.upper < fps * 0.9) {
                return false to "medido: máx. ${achievable.upper.toInt()} fps en ${h}p"
            }
            return true to ""
        }
    }

    private val cache = HashMap<String, Encoder?>()

    private fun isHardware(info: MediaCodecInfo): Boolean {
        if (Build.VERSION.SDK_INT >= 29) return info.isHardwareAccelerated && !info.isSoftwareOnly
        val n = info.name.lowercase()
        return !(n.startsWith("omx.google.") || n.startsWith("c2.android.") || n.contains(".sw.") || n.endsWith(".sw"))
    }

    /** Primer codificador por hardware para [mime], o null si no hay. */
    @Synchronized
    fun hardwareEncoder(mime: String): Encoder? = cache.getOrPut(mime) {
        val list = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
        list.filter { it.isEncoder && isHardware(it) && it.supportedTypes.any { t -> t.equals(mime, true) } }
            .firstNotNullOfOrNull { info ->
                runCatching { Encoder(info.name, mime, info, info.getCapabilitiesForType(mime)) }.getOrNull()
            }
    }

    fun supportsProfile(codecName: String, mime: String, profile: Int): Boolean {
        val e = hardwareEncoder(mime) ?: return false
        if (e.name != codecName) return false
        return e.caps.profileLevels.any { it.profile == profile }
    }
}
