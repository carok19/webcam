package com.camarandi

import android.content.Context
import android.hardware.camera2.CameraManager
import android.media.MediaFormat
import android.net.ConnectivityManager
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import android.util.Size
import android.view.Surface
import com.camarandi.camera.CameraCatalog
import com.camarandi.camera.CameraController
import com.camarandi.camera.CameraInfo
import com.camarandi.camera.Lens
import com.camarandi.camera.Preset
import com.camarandi.camera.PresetSupport
import com.camarandi.control.AdaptiveController
import com.camarandi.control.DeviceMonitor
import com.camarandi.encoder.EncoderCatalog
import com.camarandi.encoder.VideoEncoder
import com.camarandi.net.Protocol
import com.camarandi.net.StreamTransport
import org.json.JSONObject
import java.util.Locale

const val APP_VERSION = "1.0"

/** Todo lo que la pantalla necesita mostrar. */
data class EngineState(
    val streaming: Boolean,
    val live: Boolean,
    val connection: String,
    val receiver: String,
    val preset: Preset?,
    val fps: Int,
    val codec: String,
    val bitrateKbps: Int,
    val sentKbps: Double,
    val latencyMs: Double,
    val rttMs: Double,
    val thermalLevel: Int,
    val batteryPct: Int,
    val batteryTempC: Float,
    val charging: Boolean,
    val wifiBand: String,
    val zoom: Float,
    val zoomMin: Float,
    val zoomMax: Float,
    val zoomLabel: String,
    val zoomOptical: Boolean,
    val zoomLosslessUpTo: Float,
    val lenses: List<Lens>,
    val currentLensKey: String,
    val isFront: Boolean,
    val canFocus: Boolean,
    val afLocked: Boolean,
    val aeLocked: Boolean,
    val awbLocked: Boolean,
    val aeLockAvailable: Boolean,
    val awbLockAvailable: Boolean,
    val evIndex: Int,
    val evMin: Int,
    val evMax: Int,
    val evStep: Float,
    val torch: Boolean,
    val hasFlash: Boolean,
    val eisAvailable: Boolean,
    val supports: List<PresetSupport>,
    val recommended: Preset?,
    val hevcAvailable: Boolean,
    val previewSize: Size,
    val requiredReverseLandscape: Boolean,
)

/**
 * El "motor": junta cámara, codificador, red, bitrate adaptativo y temperatura.
 * Vive dentro del StreamService para seguir funcionando con la pantalla apagada.
 */
class StreamEngine(private val context: Context) :
    StreamTransport.Listener, CameraController.Listener, VideoEncoder.Sink, AdaptiveController.Listener {

    interface UiListener {
        fun onMessage(text: String, important: Boolean)
        fun onStreamingChanged(streaming: Boolean)
        fun onCameraChanged()
    }

    companion object { private const val TAG = "Motor" }

    val settings = AppSettings(context)
    private val cameraManager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
    private val connectivity = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    var catalog = CameraCatalog(cameraManager, settings.blockedCameras)
        private set
    private val cam = CameraController(cameraManager, this)
    private val transport = StreamTransport(connectivity, this)
    private val monitor = DeviceMonitor(context)
    private val adaptive = AdaptiveController(8000, this)
    private val thread = HandlerThread("motor").apply { start() }
    private val handler = Handler(thread.looper)
    private val main = Handler(Looper.getMainLooper())

    @Volatile var uiListener: UiListener? = null
    /** Para el servicio: avisa cuando empieza/termina la transmisión (p. ej. el receptor nos desconectó). */
    @Volatile var serviceListener: ((Boolean) -> Unit)? = null

    // Estado
    @Volatile private var camera: CameraInfo? = catalog.mainBack ?: catalog.cameras.firstOrNull()
    @Volatile private var lens: Lens? = catalog.backLenses.firstOrNull { it.key == "main" }
    @Volatile private var previewSurface: Surface? = null
    @Volatile private var encoder: VideoEncoder? = null
    @Volatile var streaming = false
        private set
    @Volatile private var previewWanted = false
    @Volatile private var activePreset: Preset? = null
    @Volatile private var streamPreset: Preset? = null
    @Volatile private var fps = 30
    @Volatile private var codecId = Protocol.CODEC_H264
    @Volatile private var connectionText = "En espera"
    @Volatile private var deviceStatus: DeviceMonitor.Status = monitor.read()
    @Volatile private var receiverLatencyMs = 0.0
    @Volatile private var supports: List<PresetSupport> = emptyList()
    @Volatile private var recommended: Preset? = null
    private var fpsSteps = listOf(30)
    private var resSteps = listOf<Preset>()
    private var monitorRunning = false
    private var probing = false
    private var freshStart = false

    init {
        refreshSupport()
        if (catalog.cameras.isEmpty()) post("No se encontró ninguna cámara utilizable", true)
    }

    // ================================================================= API para la interfaz

    fun state(): EngineState {
        val c = camera
        val l = lens
        val z = cam.controls.zoom
        val zi = zoomInfo(z)
        val s = deviceStatus
        val enc = encoder
        val p = streamPreset ?: activePreset
        return EngineState(
            streaming = streaming,
            live = streaming && transport.isConnected,
            connection = connectionText,
            receiver = transport.receiverName,
            preset = p,
            fps = fps,
            codec = if (codecId == Protocol.CODEC_HEVC) "HEVC" else "H.264",
            bitrateKbps = (enc?.currentBitrate ?: 0) / 1000,
            sentKbps = transport.sentKbps,
            latencyMs = if (streaming && transport.isConnected) (enc?.avgEncodeMs ?: 0.0) + transport.rttMs / 2 + receiverLatencyMs else 0.0,
            rttMs = transport.rttMs,
            thermalLevel = s.level,
            batteryPct = s.batteryPct,
            batteryTempC = s.batteryTempC,
            charging = s.charging,
            wifiBand = s.wifiBand,
            zoom = z,
            zoomMin = c?.minZoom ?: 1f,
            zoomMax = c?.maxZoom ?: 1f,
            zoomLabel = zi.first,
            zoomOptical = zi.second,
            zoomLosslessUpTo = zi.third,
            lenses = if (c?.isFront == true) emptyList() else catalog.backLenses,
            currentLensKey = l?.key ?: "",
            isFront = c?.isFront == true,
            canFocus = c?.canFocus == true,
            afLocked = cam.controls.afLocked,
            aeLocked = cam.controls.aeLocked,
            awbLocked = cam.controls.awbLocked,
            aeLockAvailable = c?.aeLockAvailable == true,
            awbLockAvailable = c?.awbLockAvailable == true,
            evIndex = cam.controls.evIndex,
            evMin = c?.evRange?.lower ?: 0,
            evMax = c?.evRange?.upper ?: 0,
            evStep = c?.evStep?.toFloat() ?: 0f,
            torch = cam.controls.torch,
            hasFlash = c?.hasFlash == true,
            eisAvailable = c?.eisAvailable == true,
            supports = supports,
            recommended = recommended,
            hevcAvailable = EncoderCatalog.hardwareEncoder(MediaFormat.MIMETYPE_VIDEO_HEVC) != null,
            previewSize = c?.previewSize() ?: Size(1280, 720),
            requiredReverseLandscape = c?.let { needsReverseLandscape(it) } ?: false,
        )
    }

    /** La app está visible: abrir cámara para la vista previa. */
    fun onUiVisible() = handler.post {
        previewWanted = true
        refreshDeviceStatus()
        probeExtraCamerasOnce()
        reconfigure()
    }

    /** La app dejó de verse: si no transmitimos, soltar la cámara (ahorra batería). */
    fun onUiHidden() = handler.post {
        previewWanted = false
        previewSurface = null
        if (!streaming) cam.close() else reconfigure()
    }

    /**
     * La vista previa se destruye (pantalla apagada, app en segundo plano). Espera a que la
     * cámara deje de usarla antes de volver, para que el flujo al codificador no se corte.
     */
    fun detachPreviewBlocking(timeoutMs: Long = 600) {
        val latch = java.util.concurrent.CountDownLatch(1)
        handler.post {
            previewSurface = null
            val enc = encoder
            if (streaming && enc?.inputSurface != null) {
                cam.configure(listOf(enc.inputSurface!!), true) { latch.countDown() }
            } else {
                cam.configure(emptyList(), false)
            }
            cam.handler.post { if (!(streaming && enc != null)) latch.countDown() }
        }
        runCatching { latch.await(timeoutMs, java.util.concurrent.TimeUnit.MILLISECONDS) }
    }

    fun setPreviewSurface(s: Surface?) = handler.post {
        if (previewSurface == s) return@post
        previewSurface = s
        reconfigure()
    }

    fun startStreaming() = handler.post {
        if (streaming) return@post
        val c = camera ?: run { post("No hay cámara disponible", true); return@post }
        streaming = true
        freshStart = true
        main.post { uiListener?.onStreamingChanged(true); serviceListener?.invoke(true) }
        transport.manualHost = settings.receiverIp
        transport.lastKnownHost = settings.lastReceiverHost
        lastEncoderKey = encoderKey()
        transport.start()
        adaptive.reset(adaptive.maxKbps) // niveles de fps/resolución a cero en cada transmisión nueva
        startEncoder(c)
        reconfigure()
        startMonitor()
    }

    fun stopStreaming(reason: String = "") = handler.post {
        if (!streaming) return@post
        streaming = false
        transport.stop()
        stopEncoder()
        reconfigure()
        connectionText = if (reason.isNotEmpty()) reason else "En espera"
        if (reason.isNotEmpty()) post(reason, true)
        main.post { uiListener?.onStreamingChanged(false); serviceListener?.invoke(false) }
        if (!previewWanted) cam.close()
    }

    fun selectLens(target: Lens) = handler.post {
        val mainCam = catalog.mainBack ?: return@post
        lens = target
        if (target.zoomRatio != null) {
            if (camera?.id != mainCam.id) switchCamera(mainCam, target.zoomRatio)
            else cam.update { zoom = target.zoomRatio }
        } else {
            val c = catalog.byId(target.cameraId) ?: return@post
            switchCamera(c, 1f)
        }
    }

    fun flipCamera() = handler.post {
        val c = camera
        val target = if (c?.isFront == true) catalog.mainBack else catalog.mainFront
        if (target == null) { post("Este celular no tiene otra cámara", false); return@post }
        lens = if (target.isBack) catalog.backLenses.firstOrNull { it.key == "main" } else null
        switchCamera(target, 1f)
    }

    fun setZoom(z: Float) {
        val c = camera ?: return
        val v = z.coerceIn(c.minZoom, c.maxZoom)
        // Mantener el chip de lente coherente con el zoom actual (en la cámara lógica).
        if (c.id == catalog.mainBack?.id) {
            lens = catalog.backLenses.filter { it.zoomRatio != null && it.zoomRatio <= v * 1.02f }.maxByOrNull { it.factor } ?: lens
        }
        cam.update { zoom = v }
    }

    fun focusAt(nx: Float, ny: Float) = cam.focusAt(nx, ny)
    fun setAfLocked(v: Boolean) = cam.setAfLocked(v)
    fun setAeLocked(v: Boolean) = cam.update { aeLocked = v }
    fun setAwbLocked(v: Boolean) = cam.update { awbLocked = v }
    fun setEv(index: Int) = cam.update { evIndex = index }
    fun setTorch(on: Boolean) = cam.update { torch = on }

    /** Se cambiaron ajustes en el panel: aplicar (reinicia el codificador si hace falta). */
    fun applySettings() = handler.post {
        cam.update { eis = settings.stabilization }
        val c = camera ?: return@post
        refreshSupport()
        val key = encoderKey()
        val changed = key != lastEncoderKey
        lastEncoderKey = key
        if (streaming && changed) {
            stopEncoder()
            startEncoder(c)
            reconfigure()
        } else if (!streaming) {
            activePreset = pickPreset(c)
            fps = activePreset?.fps ?: 30
            cam.update { fps = this@StreamEngine.fps }
        }
    }

    private var lastEncoderKey = ""
    private fun encoderKey() = "${settings.presetId}|${settings.codec}|${settings.maxMbps}|${settings.latencyMode}"

    /** Cambió la IP escrita a mano: reconectar (sin tocar cámara ni codificador). */
    fun updateReceiverIp() = handler.post {
        transport.manualHost = settings.receiverIp
        if (streaming) transport.retarget()
    }

    fun release() {
        handler.post {
            transport.stop()
            stopEncoder()
            cam.release()
        }
        thread.quitSafely()
    }

    // ================================================================= cámara

    private fun needsReverseLandscape(c: CameraInfo): Boolean =
        if (c.isFront) c.sensorOrientation == 90 else c.sensorOrientation == 270

    private fun switchCamera(c: CameraInfo, zoom: Float) {
        val old = camera
        camera = c
        cam.controls.zoom = zoom
        cam.controls.torch = cam.controls.torch && c.hasFlash
        if (old?.id == c.id) { cam.update { this.zoom = zoom }; return }
        cam.close()
        refreshSupport()
        if (streaming) {
            // ¿El preset actual funciona en la nueva cámara? Si no, reiniciar el codificador.
            val cur = streamPreset
            val ok = cur != null && supports.any { it.preset == cur && it.ok }
            if (!ok) {
                stopEncoder()
                startEncoder(c)
            }
        } else {
            activePreset = pickPreset(c)
            fps = activePreset?.fps ?: 30
        }
        cam.controls.fps = fps
        encoder?.timestampRealtime = c.timestampRealtime
        main.post { uiListener?.onCameraChanged() }
        reconfigure()
        encoder?.requestKeyframe()
    }

    private fun reconfigure() {
        val c = camera ?: return
        if (probing) return
        val wantCamera = streaming || (previewWanted && previewSurface != null)
        if (!wantCamera) {
            cam.configure(emptyList(), false)
            return
        }
        if (!cam.isOpen || cam.info?.id != c.id) {
            cam.controls.fps = fps
            cam.controls.eis = settings.stabilization
            cam.open(c) { handler.post { reconfigure() } }
            return
        }
        val surfaces = listOfNotNull(previewSurface, encoder?.inputSurface)
        cam.update { this.fps = this@StreamEngine.fps }
        cam.configure(surfaces, encoder != null)
    }

    override fun onCameraReady(info: CameraInfo) {
        Log.i(TAG, "cámara ${info.id} abierta (${info.hardwareLevelName})")
    }

    override fun onCameraError(message: String, fatal: Boolean) {
        post(message, true)
        if (fatal) return
        // Reintentar sin tocar la red ni el codificador.
        handler.postDelayed({ if (streaming || previewWanted) reconfigure() }, 1500)
    }

    /** Prueba una sola vez (por modelo de cámaras) si los lentes extra se pueden abrir. */
    private fun probeExtraCamerasOnce() {
        val fp = catalog.cameras.joinToString(",") { it.id } + "|" + Build.FINGERPRINT
        if (settings.probedFingerprint == fp || catalog.probeCandidates.isEmpty() || streaming) return
        settings.probedFingerprint = fp
        val blocked = HashSet(settings.blockedCameras)
        val pending = ArrayList(catalog.probeCandidates)
        probing = true
        fun next() {
            if (pending.isEmpty()) {
                handler.post {
                    probing = false
                    if (blocked != settings.blockedCameras) {
                        settings.blockedCameras = blocked
                        catalog = CameraCatalog(cameraManager, blocked)
                        main.post { uiListener?.onCameraChanged() }
                        post("Algunos lentes están bloqueados por el fabricante y se ocultaron", false)
                    }
                    reconfigure()
                }
                return
            }
            val id = pending.removeAt(0)
            cam.probe(id) { ok -> if (!ok) blocked += id; next() }
        }
        cam.close()
        next()
    }

    // ================================================================= codificador

    private fun refreshSupport() {
        val c = camera ?: return
        supports = Preset.ALL.map { c.evaluate(it) }
        recommended = supports.firstOrNull { it.ok }?.preset
        if (activePreset == null) {
            activePreset = pickPreset(c)
            fps = activePreset?.fps ?: 30
        }
    }

    private fun pickPreset(c: CameraInfo): Preset? {
        val wanted = Preset.byId(settings.presetId)
        val ok = supports.filter { it.ok }.map { it.preset }
        if (wanted != null && wanted in ok) return wanted
        if (wanted != null) {
            // El preset guardado no sirve en esta cámara: el mejor que no lo supere.
            val alt = ok.firstOrNull { it.pixelRate <= wanted.pixelRate } ?: ok.firstOrNull()
            post("${wanted.label} no está disponible en esta cámara; usando ${alt?.label ?: "ninguno"}", false)
            return alt
        }
        return ok.firstOrNull()
    }

    private fun autoBitrateKbps(p: Preset, hevc: Boolean): Int {
        val base = when {
            p.height >= 2160 -> 20000
            p.height >= 1440 -> 14000
            p.height >= 1080 -> if (p.fps >= 60) 12000 else 8000
            else -> if (p.fps >= 60) 6000 else 4000
        }
        return if (hevc) base else (base * 1.4).toInt()
    }

    private fun startEncoder(c: CameraInfo) {
        val chosen = pickPreset(c) ?: run {
            post("Esta cámara no soporta ninguna resolución de video con codificador por hardware", true)
            return
        }
        activePreset = chosen
        // Escalones para el bitrate adaptativo.
        fpsSteps = (listOf(chosen.fps) + c.lowerFpsSteps(chosen.fps)).take(3)
        resSteps = supports.filter { it.ok && it.preset.height < chosen.height && it.preset.fps <= chosen.fps }
            .map { it.preset }.distinctBy { it.height }
        adaptive.maxFpsLevel = fpsSteps.size - 1
        adaptive.maxResLevel = resSteps.size
        val p = if (adaptive.resLevel in 1..resSteps.size) resSteps[adaptive.resLevel - 1] else chosen
        streamPreset = p
        val support = supports.first { it.preset == p }

        val hevc = when (settings.codec) {
            "h264" -> !support.h264
            "hevc" -> support.hevc
            else -> support.hevc
        }
        val mime = if (hevc) MediaFormat.MIMETYPE_VIDEO_HEVC else MediaFormat.MIMETYPE_VIDEO_AVC
        if (settings.codec == "hevc" && !hevc) post("H.265 no disponible por hardware en ${p.label}: usando H.264", false)
        val hw = EncoderCatalog.hardwareEncoder(mime) ?: run { post("No hay codificador por hardware", true); return }
        codecId = if (hevc) Protocol.CODEC_HEVC else Protocol.CODEC_H264

        val quality = settings.latencyMode == "calidad"
        // El máximo se calcula con el preset elegido; el adaptativo ya reduce el techo por nivel de resolución.
        var maxKbps = if (settings.maxMbps > 0) settings.maxMbps * 1000 else autoBitrateKbps(chosen, hevc)
        if (quality && settings.maxMbps == 0) maxKbps = (maxKbps * 1.25).toInt()
        maxKbps = minOf(maxKbps, hw.maxBitrate / 1000)
        if (freshStart || adaptive.maxKbps != maxKbps) {
            freshStart = false
            adaptive.reset(maxKbps)
        }
        val startKbps = minOf(adaptive.bitrateKbps, maxKbps)

        fps = fpsSteps.getOrElse(adaptive.fpsLevel) { p.fps }
        val cfg = VideoEncoder.Config(
            mime = mime, codecName = hw.name, width = p.width, height = p.height, fps = p.fps,
            bitrateBps = startKbps * 1000, cbr = !quality && hw.supportsCbr,
        )
        val enc = VideoEncoder(cfg, this)
        try {
            enc.start()
            enc.timestampRealtime = c.timestampRealtime
            encoder = enc
            Log.i(TAG, "codificador ${hw.name} ${p.width}x${p.height}@${p.fps} ${startKbps} kbps cbr=${cfg.cbr}")
        } catch (e: Exception) {
            Log.e(TAG, "no se pudo iniciar el codificador", e)
            runCatching { enc.stop() }
            post("No se pudo iniciar el codificador: ${e.message}", true)
        }
    }

    private fun stopEncoder() {
        val e = encoder ?: return
        encoder = null
        // Primero sacar la superficie de la sesión de cámara, luego liberar.
        val surfaces = listOfNotNull(previewSurface)
        cam.configure(surfaces, false)
        cam.handler.post { e.stop() }
    }

    override fun onEncodedFrame(data: ByteArray, length: Int, ptsUs: Long, keyframe: Boolean) {
        val enc = encoder ?: return
        if (!transport.isConnected) return
        // Nunca mandar frames viejos (p. ej. tras una pausa): mejor saltar y pedir keyframe.
        val age = enc.ageMs(ptsUs)
        if (!keyframe && age > 500) {
            enc.requestKeyframe()
            return
        }
        transport.sendFrame(data, length, ptsUs, keyframe, codecId)
    }

    override fun onEncoderError(e: Exception) {
        Log.e(TAG, "error del codificador", e)
        handler.post {
            if (!streaming) return@post
            post("El codificador falló; reiniciando…", false)
            stopEncoder()
            handler.postDelayed({
                if (streaming) camera?.let { startEncoder(it); reconfigure() }
            }, 500)
        }
    }

    // ================================================================= red

    override fun onStateChanged(state: StreamTransport.State, detail: String) {
        connectionText = when (state) {
            StreamTransport.State.CONECTADO -> detail.ifEmpty { "Conectado" }
            StreamTransport.State.DETENIDO -> "En espera"
            else -> detail.ifEmpty { state.name }
        }
        if (state == StreamTransport.State.RECHAZADO) post(detail, true)
        if (state == StreamTransport.State.RECONECTANDO) post("Conexión perdida: reconectando (la cámara sigue activa)", false)
    }

    override fun onConnected(receiverName: String) {
        transport.target?.address?.hostAddress?.let { settings.lastReceiverHost = it }
        encoder?.requestKeyframe()
        post("Conectado a ${receiverName.ifEmpty { "receptor" }}", false)
    }

    override fun onKeyframeRequested() {
        encoder?.requestKeyframe()
    }

    override fun onReport(report: Protocol.Report, rttMs: Double, minRttMs: Double) {
        receiverLatencyMs = report.receiverLatencyMs
        handler.post { adaptive.onReport(report, rttMs, minRttMs) }
    }

    override fun onRemoteDisconnect(reason: String) {
        stopStreaming(reason)
    }

    override fun buildHello(): JSONObject {
        val p = streamPreset ?: activePreset
        val s = deviceStatus
        val enc = encoder
        return JSONObject().apply {
            put("deviceId", settings.deviceId)
            put("name", settings.deviceName)
            put("model", settings.modelName)
            put("codec", if (codecId == Protocol.CODEC_HEVC) "hevc" else "h264")
            put("width", p?.width ?: 0)
            put("height", p?.height ?: 0)
            put("fps", fps)
            put("bitrateKbps", (enc?.currentBitrate ?: 0) / 1000)
            put("maxBitrateKbps", adaptive.maxKbps)
            put("latencyMode", settings.latencyMode)
            put("streaming", streaming)
            put("thermal", s.thermalStatus)
            put("thermalLevel", s.levelName)
            put("batteryPct", s.batteryPct)
            put("batteryTempC", s.batteryTempC.toDouble())
            put("charging", s.charging)
            put("cpuPct", s.appCpuPct)
            put("encodeMs", enc?.avgEncodeMs ?: 0.0)
            put("rttMs", transport.rttMs)
            put("appVersion", APP_VERSION)
            put("lens", lens?.label ?: "")
            put("zoom", cam.controls.zoom.toDouble())
        }
    }

    // ================================================================= adaptativo

    override fun applyBitrate(kbps: Int) {
        encoder?.setBitrate(kbps * 1000)
    }

    override fun applyFpsLevel(level: Int) {
        handler.post {
            fps = fpsSteps.getOrElse(level) { fpsSteps.last() }
            cam.update { this.fps = this@StreamEngine.fps }
        }
    }

    override fun applyResolutionLevel(level: Int) {
        handler.post {
            if (!streaming) return@post
            val c = camera ?: return@post
            stopEncoder()
            startEncoder(c)
            reconfigure()
        }
    }

    override fun onAdaptMessage(msg: String) = post(msg, false)

    private fun startMonitor() {
        if (monitorRunning) return
        monitorRunning = true
        val tick = object : Runnable {
            override fun run() {
                deviceStatus = monitor.read()
                if (streaming) adaptive.onThermal(deviceStatus.level)
                if (streaming || previewWanted) handler.postDelayed(this, 1000) else monitorRunning = false
            }
        }
        handler.post(tick)
    }

    fun refreshDeviceStatus() = handler.post {
        deviceStatus = monitor.read()
        startMonitor()
    }

    // ================================================================= zoom

    /** (etiqueta, ¿óptico?, zoom digital "sin pérdida" hasta…) */
    private fun zoomInfo(z: Float): Triple<String, Boolean, Float> {
        val c = camera ?: return Triple("1.0x", true, 1f)
        val l = lens
        val isMain = c.id == catalog.mainBack?.id
        val effective = if (isMain || c.isFront) z else (l?.factor ?: 1f) * z
        val optical = when {
            isMain -> catalog.mainOpticalFactors
            c.isFront -> listOf(1f)
            else -> listOf(l?.factor ?: 1f)
        }
        val base = optical.filter { it <= effective * 1.02f }.maxOrNull() ?: optical.minOrNull() ?: 1f
        val digital = effective / base
        val encW = (streamPreset ?: activePreset)?.width ?: 1920
        val lossless = (c.activeArray.width().toFloat() / encW).coerceAtLeast(1f)
        val label = String.format(Locale.US, "%.1fx", effective)
        return Triple(label, digital <= 1.02f, lossless * base)
    }

    private fun post(text: String, important: Boolean) {
        if (text.isBlank()) return
        main.post { uiListener?.onMessage(text, important) }
    }

    fun nowMs() = SystemClock.elapsedRealtime()
}
