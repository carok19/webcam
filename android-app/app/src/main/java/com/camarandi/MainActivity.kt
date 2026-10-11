package com.camarandi

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.ComponentName
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import com.camarandi.camera.Lens
import com.camarandi.ui.AspectFrameLayout
import com.camarandi.ui.GridOverlayView
import com.camarandi.ui.SettingsPanel
import com.camarandi.ui.VerticalSlider
import java.util.Locale
import kotlin.math.ln
import kotlin.math.pow

/**
 * Pantalla única, horizontal. La vista previa ocupa todo; los controles son semitransparentes,
 * se ocultan solos a los 4 s y nunca tapan el centro del encuadre.
 */
class MainActivity : Activity(), StreamEngine.UiListener, SurfaceHolder.Callback {

    companion object {
        private const val REQ_PERMS = 10
        private const val HIDE_DELAY_MS = 4000L
    }

    private var service: StreamService? = null
    private val engine: StreamEngine? get() = service?.engine
    private val ui = Handler(Looper.getMainLooper())

    private lateinit var previewFrame: AspectFrameLayout
    private lateinit var preview: SurfaceView
    private lateinit var grid: GridOverlayView
    private lateinit var controls: View
    private lateinit var liveCorner: View
    private lateinit var liveDot: View
    private lateinit var tvLive: TextView
    private lateinit var tvRes: TextView
    private lateinit var tvBitrate: TextView
    private lateinit var tvLatency: TextView
    private lateinit var thermalDot: View
    private lateinit var tvTemp: TextView
    private lateinit var tvBattery: TextView
    private lateinit var banner: TextView
    private lateinit var tvZoom: TextView
    private lateinit var tvZoomKind: TextView
    private lateinit var zoomSlider: VerticalSlider
    private lateinit var btnTorch: ImageButton
    private lateinit var btnGrid: ImageButton
    private lateinit var btnRecord: ImageButton
    private lateinit var btnFlip: ImageButton
    private lateinit var lensChips: LinearLayout
    private lateinit var btnAf: TextView
    private lateinit var btnAe: TextView
    private lateinit var btnAwb: TextView
    private lateinit var evBox: View
    private lateinit var evSeek: SeekBar
    private lateinit var tvEv: TextView
    private lateinit var settingsPanelView: ScrollView
    private lateinit var settingsContent: LinearLayout
    private lateinit var dimOverlay: View

    private var panel: SettingsPanel? = null
    private var controlsVisible = true
    private var lastTouchMs = SystemClock.elapsedRealtime()
    private var dimmed = false
    private var surfaceReady = false
    private var draggingZoom = false
    private var draggingEv = false
    private var lastLensKeys = ""
    private var permissionsOk = false
    private var started = false

    // ------------------------------------------------------------------ ciclo de vida

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_main)
        bindViews()
        hideSystemBars()
        setupTouch()
        setupButtons()
        preview.holder.addCallback(this)
        checkPermissions()
    }

    override fun onStart() {
        super.onStart()
        started = true
        bindService(Intent(this, StreamService::class.java), connection, BIND_AUTO_CREATE)
        ui.post(tick)
    }

    override fun onStop() {
        started = false
        ui.removeCallbacks(tick)
        engine?.uiListener = null
        // Sacar la vista previa de la sesión de cámara mientras la superficie sigue siendo válida.
        engine?.detachPreviewBlocking()
        engine?.onUiHidden()
        runCatching { unbindService(connection) }
        service = null
        super.onStop()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            service = (binder as StreamService.LocalBinder).service
            val e = engine ?: return
            e.uiListener = this@MainActivity
            grid.mode = e.settings.gridMode
            if (permissionsOk) e.onUiVisible()
            if (surfaceReady) attachSurfaceIfSized()
            rebuildLensChips(true)
            showControls()
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            service = null
        }
    }

    // ------------------------------------------------------------------ permisos

    private fun checkPermissions() {
        val needed = ArrayList<String>()
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) needed += Manifest.permission.CAMERA
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            needed += Manifest.permission.POST_NOTIFICATIONS
        }
        if (needed.isEmpty()) { permissionsOk = true; return }
        requestPermissions(needed.toTypedArray(), REQ_PERMS)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        permissionsOk = checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        if (permissionsOk) engine?.onUiVisible()
        else showBanner("Sin permiso de cámara no se puede transmitir. Actívalo en Ajustes del sistema → Apps → Cámara NDI.", true)
    }

    // ------------------------------------------------------------------ vista previa

    override fun surfaceCreated(holder: SurfaceHolder) {}

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        surfaceReady = true
        attachSurfaceIfSized()
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surfaceReady = false
        engine?.detachPreviewBlocking()
    }

    private fun attachSurfaceIfSized() {
        val e = engine ?: return
        val size = e.state().previewSize
        val frame = preview.holder.surfaceFrame
        if (frame.width() != size.width || frame.height() != size.height) {
            preview.holder.setFixedSize(size.width, size.height)
            return // llegará otro surfaceChanged con el tamaño correcto
        }
        e.setPreviewSurface(preview.holder.surface)
    }

    // ------------------------------------------------------------------ UiListener

    override fun onMessage(text: String, important: Boolean) = showBanner(text, important)

    override fun onStreamingChanged(streaming: Boolean) {
        if (!streaming) exitDim()
        render()
    }

    override fun onCameraChanged() {
        rebuildLensChips(true)
        if (surfaceReady) attachSurfaceIfSized()
        panel?.let { if (settingsPanelView.visibility == View.VISIBLE) it.build() }
    }

    // ------------------------------------------------------------------ refresco de pantalla

    private val tick = object : Runnable {
        override fun run() {
            render()
            val e = engine
            if (e != null && e.streaming && !dimmed) {
                val after = e.settings.dimAfterSec
                if (after > 0 && SystemClock.elapsedRealtime() - lastTouchMs > after * 1000L) enterDim()
            }
            if (settingsPanelView.visibility == View.VISIBLE) panel?.refreshDynamic()
            ui.postDelayed(this, 300)
        }
    }

    private fun render() {
        val e = engine ?: return
        val st = e.state()

        // Orientación según cómo está montado el sensor (casi siempre "landscape").
        val wanted = if (st.requiredReverseLandscape) ActivityInfo.SCREEN_ORIENTATION_REVERSE_LANDSCAPE else ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
        if (requestedOrientation != wanted) requestedOrientation = wanted

        // Barra superior
        val liveColor = when {
            st.live -> Color.rgb(229, 57, 53)
            st.streaming -> Color.rgb(255, 160, 0)
            else -> Color.GRAY
        }
        liveDot.background.setTint(liveColor)
        tvLive.text = when {
            st.live -> "EN VIVO"
            st.streaming -> shortConn(st.connection)
            else -> "En espera"
        }
        tvRes.text = st.preset?.let { "${it.shortRes}${st.fps} ${st.codec}" } ?: "-"
        tvBitrate.text = if (st.streaming) String.format(Locale.US, "%.1f Mbps", st.sentKbps / 1000) else
            (st.bitrateKbps.takeIf { it > 0 }?.let { String.format(Locale.US, "%.1f Mbps", it / 1000.0) } ?: "")
        tvBitrate.visibility = if (tvBitrate.text.isEmpty()) View.GONE else View.VISIBLE
        tvLatency.text = if (st.live && st.latencyMs > 0) String.format(Locale.US, "≈%.0f ms", st.latencyMs) else ""
        tvLatency.visibility = if (tvLatency.text.isEmpty()) View.GONE else View.VISIBLE
        thermalDot.background.setTint(
            when (st.thermalLevel) { 2 -> Color.rgb(229, 57, 53); 1 -> Color.rgb(255, 202, 40); else -> Color.rgb(67, 160, 71) }
        )
        tvTemp.text = if (st.batteryTempC > 0) String.format(Locale.US, "%.0f°C", st.batteryTempC) else ""
        tvBattery.text = if (st.batteryPct >= 0) "${st.batteryPct}%" + (if (st.charging) "⚡" else "") else ""

        // Zoom
        tvZoom.text = st.zoomLabel
        val digital = !st.zoomOptical
        tvZoomKind.text = if (digital) "digital" else "óptico"
        val zoomVal = st.zoomLabel.removeSuffix("x").toFloatOrNull() ?: 1f
        tvZoomKind.setTextColor(
            if (digital && zoomVal > st.zoomLosslessUpTo + 0.05f) Color.rgb(255, 160, 0) else Color.argb(200, 255, 255, 255)
        )
        if (!draggingZoom) zoomSlider.value = zoomToSlider(st.zoom, st.zoomMin, st.zoomMax)
        zoomSlider.visibility = if (st.zoomMax > st.zoomMin + 0.01f) View.VISIBLE else View.INVISIBLE

        // Botones
        btnTorch.visibility = if (st.hasFlash) View.VISIBLE else View.GONE
        btnTorch.setBackgroundResource(if (st.torch) R.drawable.bg_round_active else R.drawable.bg_round)
        btnGrid.setBackgroundResource(if (grid.mode != 0) R.drawable.bg_round_active else R.drawable.bg_round)
        btnRecord.setBackgroundResource(if (st.streaming) R.drawable.bg_record_live else R.drawable.bg_record_idle)
        btnRecord.contentDescription = if (st.streaming) "Detener" else "Transmitir"
        chip(btnAf, st.afLocked, st.canFocus)
        chip(btnAe, st.aeLocked, st.aeLockAvailable)
        chip(btnAwb, st.awbLocked, st.awbLockAvailable)
        evBox.visibility = if (st.evMax > st.evMin) View.VISIBLE else View.GONE
        if (!draggingEv && st.evMax > st.evMin) {
            evSeek.max = st.evMax - st.evMin
            evSeek.progress = st.evIndex - st.evMin
            tvEv.text = String.format(Locale.US, "%+.1f", st.evIndex * st.evStep)
        }
        rebuildLensChips(false)
        liveCorner.visibility = if (!controlsVisible && st.live) View.VISIBLE else View.GONE
    }

    private fun shortConn(s: String): String = if (s.length > 28) s.take(27) + "…" else s

    private fun chip(v: TextView, on: Boolean, available: Boolean) {
        v.visibility = if (available) View.VISIBLE else View.GONE
        v.setBackgroundResource(if (on) R.drawable.bg_chip_on else R.drawable.bg_chip)
        v.setTextColor(if (on) Color.BLACK else Color.WHITE)
    }

    private fun rebuildLensChips(force: Boolean) {
        val e = engine ?: return
        val st = e.state()
        val keys = st.lenses.joinToString { it.key } + "|" + st.currentLensKey
        if (!force && keys == lastLensKeys) return
        lastLensKeys = keys
        lensChips.removeAllViews()
        if (st.lenses.size <= 1) return
        for (l in st.lenses) {
            val tv = TextView(this, null, 0, R.style.Chip).apply {
                text = l.label
                contentDescription = "Lente ${l.kind}"
                val on = l.key == st.currentLensKey
                setBackgroundResource(if (on) R.drawable.bg_chip_on else R.drawable.bg_chip)
                setTextColor(if (on) Color.BLACK else Color.WHITE)
                setOnClickListener { selectLens(l) }
            }
            val lp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            val m = (4 * resources.displayMetrics.density).toInt()
            lp.setMargins(m, 0, m, 0)
            lensChips.addView(tv, lp)
        }
    }

    private fun selectLens(l: Lens) {
        engine?.selectLens(l)
        showBanner("Lente ${l.label} (${l.kind})", false)
    }

    // ------------------------------------------------------------------ botones

    private fun setupButtons() {
        btnRecord.setOnClickListener {
            val e = engine ?: return@setOnClickListener
            if (e.streaming) {
                e.stopStreaming()
            } else {
                if (!permissionsOk) { checkPermissions(); return@setOnClickListener }
                startStreamingWithTips()
            }
        }
        btnFlip.setOnClickListener { engine?.flipCamera() }
        btnGrid.setOnClickListener {
            grid.mode = (grid.mode + 1) % GridOverlayView.NAMES.size
            engine?.settings?.gridMode = grid.mode
            showBanner(GridOverlayView.NAMES[grid.mode] + " (sólo en el celular, no se transmite)", false)
            render()
        }
        btnTorch.setOnClickListener { engine?.let { it.setTorch(!it.state().torch) } }
        btnAf.setOnClickListener { engine?.let { val v = !it.state().afLocked; it.setAfLocked(v); showBanner(if (v) "Enfoque bloqueado" else "Enfoque automático continuo", false) } }
        btnAe.setOnClickListener { engine?.let { val v = !it.state().aeLocked; it.setAeLocked(v); showBanner(if (v) "Exposición bloqueada" else "Exposición automática", false) } }
        btnAwb.setOnClickListener { engine?.let { val v = !it.state().awbLocked; it.setAwbLocked(v); showBanner(if (v) "Balance de blancos bloqueado" else "Balance de blancos automático", false) } }
        evSeek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar, p: Int, fromUser: Boolean) {
                if (!fromUser) return
                val st = engine?.state() ?: return
                val idx = p + st.evMin
                engine?.setEv(idx)
                tvEv.text = String.format(Locale.US, "%+.1f", idx * st.evStep)
            }
            override fun onStartTrackingTouch(sb: SeekBar) { draggingEv = true }
            override fun onStopTrackingTouch(sb: SeekBar) { draggingEv = false }
        })
        zoomSlider.onChange = { v ->
            draggingZoom = true
            engine?.let { e -> val st = e.state(); e.setZoom(sliderToZoom(v, st.zoomMin, st.zoomMax)) }
        }
        zoomSlider.onTouchActivity = { ui.removeCallbacks(endZoomDrag); ui.postDelayed(endZoomDrag, 400) }
        findViewById<View>(R.id.btnSettings).setOnClickListener { toggleSettings() }
        dimOverlay.setOnClickListener { exitDim() }
    }

    private val endZoomDrag = Runnable { draggingZoom = false }

    private fun startStreamingWithTips() {
        val e = engine ?: return
        val st = e.state()
        val warnings = ArrayList<String>()
        if (!e.settings.tipsShown) {
            warnings += "Para sesiones largas usa el celular sin funda, con el cargador conectado y en un lugar ventilado."
            e.settings.tipsShown = true
        }
        if (st.wifiBand == "2.4 GHz") warnings += "Estás conectado al WiFi de 2.4 GHz. Para menos latencia usa la red de 5 GHz del router."
        if (warnings.isNotEmpty()) {
            AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("Antes de transmitir")
                .setMessage(warnings.joinToString("\n\n"))
                .setPositiveButton("Transmitir") { _, _ -> startService() }
                .setNegativeButton("Cancelar", null)
                .show()
        } else {
            startService()
        }
    }

    private fun startService() {
        val i = Intent(this, StreamService::class.java).setAction(StreamService.ACTION_START)
        startForegroundService(i)
    }

    private fun toggleSettings() {
        val e = engine ?: return
        if (settingsPanelView.visibility == View.VISIBLE) {
            settingsPanelView.visibility = View.GONE
            hideSystemBars()
            scheduleHide()
            return
        }
        panel = SettingsPanel(this, settingsContent, e, onClose = { toggleSettings() }, onDimNow = { toggleSettings(); enterDim() })
        panel?.build()
        settingsPanelView.visibility = View.VISIBLE
        ui.removeCallbacks(hideRunnable)
    }

    // ------------------------------------------------------------------ toques

    private fun setupTouch() {
        val scale = ScaleGestureDetector(this, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            private var z = 1f
            override fun onScaleBegin(d: ScaleGestureDetector): Boolean {
                z = engine?.state()?.zoom ?: 1f
                return true
            }
            override fun onScale(d: ScaleGestureDetector): Boolean {
                val e = engine ?: return false
                val st = e.state()
                z = (z * d.scaleFactor).coerceIn(st.zoomMin, st.zoomMax)
                e.setZoom(z)
                return true
            }
        })
        val gestures = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onDown(e: MotionEvent) = true

            override fun onSingleTapUp(ev: MotionEvent): Boolean {
                val inFrame = inPreview(ev.x, ev.y)
                if (!controlsVisible) {
                    showControls()
                    if (inFrame) focusAt(ev.x, ev.y, lock = false)
                } else if (inFrame && inCentralArea(ev.x, ev.y)) {
                    focusAt(ev.x, ev.y, lock = false)
                    scheduleHide()
                } else {
                    hideControls()
                }
                return true
            }

            override fun onLongPress(ev: MotionEvent) {
                if (inPreview(ev.x, ev.y)) focusAt(ev.x, ev.y, lock = true)
            }
        })
        findViewById<View>(R.id.touchLayer).setOnTouchListener { _, ev ->
            scale.onTouchEvent(ev)
            if (!scale.isInProgress) gestures.onTouchEvent(ev)
            true
        }
    }

    private fun inPreview(x: Float, y: Float): Boolean =
        x >= previewFrame.left && x <= previewFrame.right && y >= previewFrame.top && y <= previewFrame.bottom

    /** Zona central (60 % x 60 %) donde el toque enfoca aunque los controles estén visibles. */
    private fun inCentralArea(x: Float, y: Float): Boolean {
        val w = previewFrame.width; val h = previewFrame.height
        val nx = (x - previewFrame.left) / w; val ny = (y - previewFrame.top) / h
        return nx in 0.2f..0.8f && ny in 0.2f..0.8f
    }

    private fun focusAt(x: Float, y: Float, lock: Boolean) {
        val e = engine ?: return
        if (!e.state().canFocus) return
        val nx = (x - previewFrame.left) / previewFrame.width
        val ny = (y - previewFrame.top) / previewFrame.height
        e.focusAt(nx, ny)
        grid.showFocus(x - previewFrame.left, y - previewFrame.top, lock)
        if (lock) {
            e.setAfLocked(true)
            if (e.state().aeLockAvailable) e.setAeLocked(true)
            showBanner("AE/AF bloqueados en ese punto", false)
        }
    }

    override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
        if (ev.actionMasked == MotionEvent.ACTION_DOWN) {
            lastTouchMs = SystemClock.elapsedRealtime()
            if (controlsVisible && settingsPanelView.visibility != View.VISIBLE) scheduleHide()
        }
        return super.dispatchTouchEvent(ev)
    }

    // ------------------------------------------------------------------ ocultar controles

    private val hideRunnable = Runnable { hideControls() }

    private fun scheduleHide() {
        ui.removeCallbacks(hideRunnable)
        ui.postDelayed(hideRunnable, HIDE_DELAY_MS)
    }

    private fun showControls() {
        controlsVisible = true
        controls.animate().cancel()
        controls.visibility = View.VISIBLE
        controls.animate().alpha(1f).setDuration(150).start()
        scheduleHide()
        render()
    }

    private fun hideControls() {
        if (settingsPanelView.visibility == View.VISIBLE) return
        controlsVisible = false
        controls.animate().cancel()
        controls.animate().alpha(0f).setDuration(250).withEndAction {
            if (!controlsVisible) controls.visibility = View.INVISIBLE
        }.start()
        render()
    }

    // ------------------------------------------------------------------ modo ahorro

    private fun enterDim() {
        if (dimmed) return
        dimmed = true
        dimOverlay.visibility = View.VISIBLE
        window.attributes = window.attributes.apply { screenBrightness = 0.01f }
    }

    private fun exitDim() {
        if (!dimmed) return
        dimmed = false
        lastTouchMs = SystemClock.elapsedRealtime()
        dimOverlay.visibility = View.GONE
        window.attributes = window.attributes.apply { screenBrightness = WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE }
        showControls()
    }

    // ------------------------------------------------------------------ utilidades

    private fun zoomToSlider(z: Float, min: Float, max: Float): Float =
        if (max <= min) 0f else (ln(z / min) / ln(max / min)).toFloat().coerceIn(0f, 1f)

    private fun sliderToZoom(v: Float, min: Float, max: Float): Float =
        if (max <= min) min else (min * (max / min).toDouble().pow(v.toDouble())).toFloat()

    private val hideBanner = Runnable { banner.visibility = View.GONE }

    private fun showBanner(text: String, important: Boolean) {
        banner.text = text
        banner.setTextColor(if (important) Color.rgb(255, 205, 210) else Color.WHITE)
        banner.visibility = View.VISIBLE
        ui.removeCallbacks(hideBanner)
        ui.postDelayed(hideBanner, if (important) 6000 else 3000)
    }

    @Suppress("DEPRECATION")
    private fun hideSystemBars() {
        if (Build.VERSION.SDK_INT >= 30) {
            window.insetsController?.let {
                it.hide(WindowInsets.Type.systemBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_FULLSCREEN or
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN)
        }
    }

    private fun bindViews() {
        previewFrame = findViewById(R.id.previewFrame)
        preview = findViewById(R.id.preview)
        grid = findViewById(R.id.grid)
        controls = findViewById(R.id.controls)
        liveCorner = findViewById(R.id.liveCorner)
        liveDot = findViewById(R.id.liveDot)
        tvLive = findViewById(R.id.tvLive)
        tvRes = findViewById(R.id.tvRes)
        tvBitrate = findViewById(R.id.tvBitrate)
        tvLatency = findViewById(R.id.tvLatency)
        thermalDot = findViewById(R.id.thermalDot)
        tvTemp = findViewById(R.id.tvTemp)
        tvBattery = findViewById(R.id.tvBattery)
        banner = findViewById(R.id.banner)
        tvZoom = findViewById(R.id.tvZoom)
        tvZoomKind = findViewById(R.id.tvZoomKind)
        zoomSlider = findViewById(R.id.zoomSlider)
        btnTorch = findViewById(R.id.btnTorch)
        btnGrid = findViewById(R.id.btnGrid)
        btnRecord = findViewById(R.id.btnRecord)
        btnFlip = findViewById(R.id.btnFlip)
        lensChips = findViewById(R.id.lensChips)
        btnAf = findViewById(R.id.btnAf)
        btnAe = findViewById(R.id.btnAe)
        btnAwb = findViewById(R.id.btnAwb)
        evBox = findViewById(R.id.evBox)
        evSeek = findViewById(R.id.evSeek)
        tvEv = findViewById(R.id.tvEv)
        settingsPanelView = findViewById(R.id.settingsPanel)
        settingsContent = findViewById(R.id.settingsContent)
        dimOverlay = findViewById(R.id.dimOverlay)
        // Las tintas se aplican a copias para no compartir el drawable entre vistas.
        liveDot.background = liveDot.background.mutate()
        thermalDot.background = thermalDot.background.mutate()
    }
}
