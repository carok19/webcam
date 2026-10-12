package com.camarandi

import android.content.Context
import android.os.Build
import android.provider.Settings
import java.util.UUID

/** Preferencias guardadas en el celular. */
class AppSettings(context: Context) {
    private val p = context.getSharedPreferences("camarandi", Context.MODE_PRIVATE)
    private val resolver = context.contentResolver

    val deviceId: String = p.getString("deviceId", null) ?: UUID.randomUUID().toString().also {
        p.edit().putString("deviceId", it).apply()
    }

    var deviceName: String
        get() = p.getString("deviceName", "Cel 1") ?: "Cel 1"
        set(v) = p.edit().putString("deviceName", v.trim().ifEmpty { "Cel 1" }).apply()

    /** IP del receptor escrita a mano. Vacío = descubrimiento automático. */
    var receiverIp: String
        get() = p.getString("receiverIp", "") ?: ""
        set(v) = p.edit().putString("receiverIp", v.trim()).apply()

    var lastReceiverHost: String
        get() = p.getString("lastHost", "") ?: ""
        set(v) = p.edit().putString("lastHost", v).apply()

    /** Id del preset (p. ej. "1080p30"). Vacío = el mejor que soporte el celular. */
    var presetId: String
        get() = p.getString("preset", "") ?: ""
        set(v) = p.edit().putString("preset", v).apply()

    /** "auto", "hevc" o "h264". */
    var codec: String
        get() = p.getString("codec", "auto") ?: "auto"
        set(v) = p.edit().putString("codec", v).apply()

    /** Bitrate máximo en Mbps. 0 = automático según resolución. */
    var maxMbps: Int
        get() = p.getInt("maxMbps", 0)
        set(v) = p.edit().putInt("maxMbps", v).apply()

    /**
     * "minima" (~40 ms de buffer), "equilibrada" (~70 ms), "calidad" (~300 ms) o "maxima" (~1 s).
     * Por defecto "calidad": en la iglesia importa más que se vea bien que unos milisegundos.
     */
    var latencyMode: String
        get() = p.getString("latency", "calidad") ?: "calidad"
        set(v) = p.edit().putString("latency", v).apply()

    val isQualityMode: Boolean get() = latencyMode == "calidad" || latencyMode == "maxima"

    /** "celular" (curva del fabricante) o "neutral" (curva estándar sRGB, más detalle en sombras). */
    var toneCurve: String
        get() = p.getString("tone", "celular") ?: "celular"
        set(v) = p.edit().putString("tone", v).apply()

    /** Medir la exposición en el centro (la persona) en vez de en toda la imagen. */
    var meterCenter: Boolean
        get() = p.getBoolean("meterCenter", false)
        set(v) = p.edit().putBoolean("meterCenter", v).apply()

    /** Compensación de exposición guardada (en pasos de la cámara). */
    var evIndex: Int
        get() = p.getInt("ev", 0)
        set(v) = p.edit().putInt("ev", v).apply()

    var stabilization: Boolean
        get() = p.getBoolean("eis", false)
        set(v) = p.edit().putBoolean("eis", v).apply()

    /** Segundos sin tocar para atenuar la pantalla mientras transmite. 0 = nunca. */
    var dimAfterSec: Int
        get() = p.getInt("dimAfter", 0)
        set(v) = p.edit().putInt("dimAfter", v).apply()

    var gridMode: Int
        get() = p.getInt("grid", 0)
        set(v) = p.edit().putInt("grid", v).apply()

    var blockedCameras: Set<String>
        get() = p.getStringSet("blockedCams", emptySet()) ?: emptySet()
        set(v) = p.edit().putStringSet("blockedCams", v).apply()

    var probedFingerprint: String
        get() = p.getString("probedFp", "") ?: ""
        set(v) = p.edit().putString("probedFp", v).apply()

    /** Lo que decodifica la GPU del último PC receptor (-1 = no se sabe). Para arrancar ya con el códec correcto. */
    var lastHevcGpuMaxHeight: Int
        get() = p.getInt("pcHevc", -1)
        set(v) = p.edit().putInt("pcHevc", v).apply()

    var lastH264GpuMaxHeight: Int
        get() = p.getInt("pcH264", -1)
        set(v) = p.edit().putInt("pcH264", v).apply()

    var tipsShown: Boolean
        get() = p.getBoolean("tips", false)
        set(v) = p.edit().putBoolean("tips", v).apply()

    /** Nombre comercial del celular ("Galaxy A52s 5G") si el sistema lo da; si no, el modelo. */
    val modelName: String
        get() {
            val n = runCatching { Settings.Global.getString(resolver, "device_name") }.getOrNull()
            return if (!n.isNullOrBlank() && !n.equals(Build.MODEL, true)) n else "${Build.MANUFACTURER} ${Build.MODEL}"
        }
}
