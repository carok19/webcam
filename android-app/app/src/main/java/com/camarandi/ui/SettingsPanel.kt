package com.camarandi.ui

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.graphics.Color
import android.net.Uri
import android.os.PowerManager
import android.provider.Settings
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.SeekBar
import android.widget.Switch
import android.widget.TextView
import com.camarandi.R
import com.camarandi.StreamEngine
import com.camarandi.camera.formatFactor
import com.camarandi.encoder.EncoderCatalog
import android.media.MediaFormat

/**
 * Panel lateral de ajustes (no es otra pantalla). Se construye en código porque la lista de
 * resoluciones depende de lo que el celular soporta de verdad.
 */
class SettingsPanel(
    private val activity: Activity,
    private val container: LinearLayout,
    private val engine: StreamEngine,
    private val onClose: () -> Unit,
    private val onDimNow: () -> Unit,
) {
    private val s = engine.settings
    private val d = activity.resources.displayMetrics.density
    private var connectionView: TextView? = null
    private var infoView: TextView? = null

    fun build() {
        container.removeAllViews()
        val st = engine.state()

        title("Ajustes")

        // --- Resolución y fps
        section("Resolución y fps")
        val presetGroup = RadioGroup(activity)
        val autoLabel = "Automática: la mejor que sostiene (${st.recommended?.label ?: "ninguna"})"
        presetGroup.addView(radio(autoLabel, s.presetId.isEmpty()) { s.presetId = ""; engine.applySettings() })
        val ok = st.supports.filter { it.ok }
        for (sup in ok) {
            val p = sup.preset
            val codecs = listOfNotNull(if (sup.hevc) "H.265" else null, if (sup.h264) "H.264" else null).joinToString("/")
            val rec = if (p == st.recommended) " · recomendada" else ""
            presetGroup.addView(radio("${p.label} (${p.width}x${p.height}) · $codecs$rec", s.presetId == p.id) {
                s.presetId = p.id; engine.applySettings()
            })
        }
        container.addView(presetGroup)
        val missing = st.supports.filter { !it.ok }
        if (missing.isNotEmpty()) {
            hint("No disponibles en esta cámara: " + missing.joinToString("; ") { "${it.preset.label} (${it.reason})" })
        }
        hint("Cambiarlo durante la transmisión reinicia el video un instante.")

        // --- Códec
        section("Códec")
        val hevcOk = EncoderCatalog.hardwareEncoder(MediaFormat.MIMETYPE_VIDEO_HEVC) != null
        val codecGroup = RadioGroup(activity)
        codecGroup.addView(radio("Automático (H.265 si hay hardware)", s.codec == "auto") { s.codec = "auto"; engine.applySettings() })
        codecGroup.addView(radio(if (hevcOk) "H.265 / HEVC" else "H.265 / HEVC (no disponible)", s.codec == "hevc", hevcOk) { s.codec = "hevc"; engine.applySettings() })
        codecGroup.addView(radio("H.264", s.codec == "h264") { s.codec = "h264"; engine.applySettings() })
        container.addView(codecGroup)

        // --- Bitrate máximo
        section("Bitrate máximo")
        val brLabel = text("")
        fun brText(v: Int) = if (v == 0) "Automático (según resolución)" else "$v Mbps"
        brLabel.text = brText(s.maxMbps)
        val br = SeekBar(activity).apply {
            max = 40
            progress = s.maxMbps
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(sb: SeekBar, v: Int, fromUser: Boolean) { brLabel.text = brText(v) }
                override fun onStartTrackingTouch(sb: SeekBar) {}
                override fun onStopTrackingTouch(sb: SeekBar) { s.maxMbps = sb.progress; engine.applySettings() }
            })
        }
        container.addView(br, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, (40 * d).toInt()))
        hint("Es un techo: el bitrate baja solo si la red se satura o el celular se calienta, y vuelve a subir poco a poco.")

        // --- Latencia / calidad
        section("Modo: rapidez o calidad")
        val lat = RadioGroup(activity)
        lat.addView(radio("Máxima calidad (≈1 s de retraso)", s.latencyMode == "maxima") { s.latencyMode = "maxima"; engine.applySettings(); build() })
        lat.addView(radio("Calidad (≈0,3 s) · recomendado", s.latencyMode == "calidad") { s.latencyMode = "calidad"; engine.applySettings(); build() })
        lat.addView(radio("Equilibrado (≈0,1 s)", s.latencyMode == "equilibrada") { s.latencyMode = "equilibrada"; engine.applySettings(); build() })
        lat.addView(radio("Mínima latencia (≈0,08 s)", s.latencyMode == "minima") { s.latencyMode = "minima"; engine.applySettings(); build() })
        container.addView(lat)
        hint(
            if (s.isQualityMode) "Calidad: más bitrate, el codificador reparte los bits donde hacen falta (sombras, ropa oscura, zoom), " +
                "procesamiento de imagen de alta calidad y un buffer grande en el PC que recupera los paquetes perdidos " +
                "en vez de saltar frames. Prefiere 30 fps (con poca luz, 60 fps llena las sombras de ruido)."
            else "Baja latencia: sirve si necesitas ver la imagen casi al instante; con poca luz o mucho movimiento se ve peor."
        )

        // --- Imagen
        section("Imagen (sin filtros)")
        val cam = engine.catalog.mainBack
        val neutralOk = cam?.supportsNeutralTone == true
        val tone = RadioGroup(activity)
        tone.addView(radio("Curva del celular (por defecto)", s.toneCurve != "neutral") { s.toneCurve = "celular"; engine.applySettings() })
        tone.addView(radio(if (neutralOk) "Curva neutral sRGB: más detalle en negros" else "Curva neutral (este celular no la permite)", s.toneCurve == "neutral" && neutralOk, neutralOk) {
            s.toneCurve = "neutral"; engine.applySettings()
        })
        container.addView(tone)
        hint("La curva del fabricante suele dar más contraste y \"aplastar\" la ropa negra. La neutral es la estándar de las pantallas: no agrega nada, sólo no oscurece de más las sombras.")
        val meter = RadioGroup(activity)
        meter.addView(radio("Exposición según toda la imagen", !s.meterCenter) { s.meterCenter = false; engine.applySettings() })
        meter.addView(radio("Exposición según el centro (la persona)", s.meterCenter) { s.meterCenter = true; engine.applySettings() })
        container.addView(meter)
        hint("Con ventanas o luces fuertes detrás del púlpito, medir en el centro evita que la persona y la ropa oscura queden subexpuestas. El botón EV (abajo) sube o baja la exposición y se recuerda.")

        // --- Nombre
        section("Nombre del dispositivo")
        container.addView(edit(s.deviceName, "Cel 1", InputType.TYPE_CLASS_TEXT) { s.deviceName = it })
        hint("Aparece en el receptor y en OBS como \"${s.deviceName} - ${s.modelName}\".")

        // --- IP
        section("IP del receptor")
        container.addView(edit(s.receiverIp, "Automático (déjalo vacío)", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI) {
            s.receiverIp = it
            engine.updateReceiverIp()
        })
        connectionView = hint("")

        // --- Estabilización
        section("Estabilización")
        val sw = Switch(activity).apply {
            text = if (st.eisAvailable) "Estabilización electrónica" else "Estabilización electrónica (no disponible)"
            setTextColor(Color.WHITE)
            isChecked = s.stabilization && st.eisAvailable
            isEnabled = st.eisAvailable
            minHeight = (48 * d).toInt()
            setOnCheckedChangeListener { _, v -> s.stabilization = v; engine.applySettings() }
        }
        container.addView(sw)
        hint("La electrónica puede añadir latencia (≈1 frame) y recorta un poco la imagen. La óptica (OIS) se usa siempre que exista.")

        // --- Pantalla
        section("Pantalla durante la transmisión")
        val dim = RadioGroup(activity)
        dim.addView(radio("No atenuar", s.dimAfterSec == 0) { s.dimAfterSec = 0 })
        dim.addView(radio("Atenuar tras 30 s sin tocar", s.dimAfterSec == 30) { s.dimAfterSec = 30 })
        dim.addView(radio("Atenuar tras 2 min sin tocar", s.dimAfterSec == 120) { s.dimAfterSec = 120 })
        container.addView(dim)
        button("Modo ahorro ahora") { onDimNow() }
        hint("También puedes apagar la pantalla con el botón de encendido: la transmisión sigue (aviso fijo en la barra de notificaciones).")

        // --- Batería
        section("Segundo plano")
        val pm = activity.getSystemService(PowerManager::class.java)
        val unrestricted = pm.isIgnoringBatteryOptimizations(activity.packageName)
        button(if (unrestricted) "Sin restricciones de batería ✔" else "Permitir sin restricciones de batería") { requestUnrestricted() }
        hint("Recomendado en Samsung (Batería → Uso en segundo plano) e Infinix/XOS (Ahorro de energía → apps en segundo plano) para que el sistema no corte la transmisión con la pantalla apagada.")

        // --- Información
        section("Información del celular")
        infoView = hint("")
        refreshInfo()

        button("Cerrar") { onClose() }
    }

    fun refreshDynamic() {
        val st = engine.state()
        connectionView?.text = "Estado: ${st.connection}" + if (st.receiver.isNotEmpty()) " · receptor: ${st.receiver}" else ""
        refreshInfo()
    }

    private fun refreshInfo() {
        val st = engine.state()
        val cat = engine.catalog
        val sb = StringBuilder()
        val c = cat.mainBack
        if (c != null) sb.append("Cámara trasera ${c.id}: nivel ${c.hardwareLevelName}, sensor ${c.activeArray.width()}x${c.activeArray.height()}\n")
        sb.append("Lentes: ").append(cat.backLenses.joinToString { "${it.label} ${it.kind}" }).append('\n')
        sb.append("Zoom: ${formatFactor(st.zoomMin)} a ${formatFactor(st.zoomMax)}; recorte casi sin pérdida hasta ≈${formatFactor(st.zoomLosslessUpTo)} en ${st.preset?.shortRes ?: "-"}\n")
        cat.notes.forEach { sb.append("• ").append(it).append('\n') }
        val hevc = EncoderCatalog.hardwareEncoder(MediaFormat.MIMETYPE_VIDEO_HEVC)
        val avc = EncoderCatalog.hardwareEncoder(MediaFormat.MIMETYPE_VIDEO_AVC)
        sb.append("Codificador H.265: ${hevc?.name ?: "no hay"}\nCodificador H.264: ${avc?.name ?: "no hay"}\n")
        sb.append("WiFi: ${st.wifiBand}")
        if (st.wifiBand == "2.4 GHz") sb.append("  ⚠ usa la red de 5 GHz del router para menos latencia")
        infoView?.text = sb.toString()
    }

    @SuppressLint("BatteryLife")
    private fun requestUnrestricted() {
        runCatching {
            activity.startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:${activity.packageName}")))
        }.onFailure {
            runCatching { activity.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)) }
        }
    }

    // ------------------------------------------------------------ helpers de vistas

    private fun title(t: String) {
        container.addView(TextView(activity).apply {
            text = t
            setTextColor(Color.WHITE)
            textSize = 18f
        })
    }

    private fun section(t: String) {
        container.addView(TextView(activity, null, 0, R.style.PanelTitle).apply { text = t }, lp().apply {
            topMargin = (14 * d).toInt(); bottomMargin = (4 * d).toInt()
        })
    }

    private fun text(t: String): TextView {
        val v = TextView(activity, null, 0, R.style.PanelText).apply { text = t }
        container.addView(v)
        return v
    }

    private fun hint(t: String): TextView {
        val v = TextView(activity, null, 0, R.style.PanelHint).apply { text = t }
        container.addView(v, lp().apply { topMargin = (2 * d).toInt() })
        return v
    }

    private fun radio(label: String, checked: Boolean, enabled: Boolean = true, onPick: () -> Unit): RadioButton =
        RadioButton(activity).apply {
            id = View.generateViewId()
            text = label
            textSize = 13f
            setTextColor(Color.WHITE)
            isChecked = checked
            isEnabled = enabled
            minHeight = (44 * d).toInt()
            setOnClickListener { onPick() }
        }

    private fun edit(value: String, hintText: String, type: Int, onChange: (String) -> Unit): EditText =
        EditText(activity).apply {
            setText(value)
            hint = hintText
            inputType = type
            setSingleLine()
            setTextColor(Color.WHITE)
            setHintTextColor(Color.GRAY)
            textSize = 14f
            minHeight = (48 * d).toInt()
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun afterTextChanged(e: Editable?) { onChange(e?.toString() ?: "") }
            })
        }

    private fun button(label: String, onClick: () -> Unit) {
        container.addView(Button(activity).apply {
            text = label
            isAllCaps = false
            gravity = Gravity.CENTER
            minHeight = (48 * d).toInt()
            setOnClickListener { onClick() }
        }, lp().apply { topMargin = (6 * d).toInt() })
    }

    private fun lp() = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
}
