package com.camarandi.camera

import android.annotation.SuppressLint
import android.graphics.Rect
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.params.MeteringRectangle
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import android.view.Surface

/**
 * Maneja Camera2 en su propio hilo: abrir, crear la sesión con 1 o 2 superficies
 * (vista previa y/o codificador) y aplicar los controles (zoom, enfoque, bloqueos, linterna…).
 */
class CameraController(
    private val manager: CameraManager,
    private val listener: Listener,
) {
    interface Listener {
        fun onCameraReady(info: CameraInfo)
        fun onCameraError(message: String, fatal: Boolean)
    }

    /** Controles deseados. Se aplican en cada petición repetida. */
    data class Controls(
        var zoom: Float = 1f,
        var fps: Int = 30,
        var afLocked: Boolean = false,
        var aeLocked: Boolean = false,
        var awbLocked: Boolean = false,
        var evIndex: Int = 0,
        var torch: Boolean = false,
        var eis: Boolean = false,
        /** Modos de calidad: reducción de ruido y nitidez de alta calidad. */
        var quality: Boolean = false,
        /** Curva de tono estándar sRGB en vez de la del fabricante (más detalle en sombras). */
        var neutralTone: Boolean = false,
        /** Medir la exposición en el centro (la persona) y no en toda la imagen. */
        var meterCenter: Boolean = false,
    )

    companion object { private const val TAG = "Camara" }

    private val thread = HandlerThread("camara").apply { start() }
    val handler = Handler(thread.looper)

    var info: CameraInfo? = null
        private set
    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var surfaces: List<Surface> = emptyList()
    private var recording = false
    private var openingId: String? = null
    private val pendingOpened = ArrayList<() -> Unit>()
    private var sessionGeneration = 0
    /** Superficie del codificador con la que falló "vista previa + video": se transmite sin vista previa. */
    private var dropPreviewFor: Surface? = null
    /** Si el HAL rechazó la sesión con "stream use cases", no se vuelven a usar. */
    private var useCasesFailed = false

    val controls = Controls()
    private var focusRegion: MeteringRectangle? = null
    private var focusResetToken = 0

    val isOpen get() = device != null

    // ------------------------------------------------------------- abrir / cerrar

    @SuppressLint("MissingPermission")
    fun open(target: CameraInfo, onOpened: (() -> Unit)? = null) = handler.post {
        if (device != null && info?.id == target.id) { onOpened?.invoke(); return@post }
        if (openingId == target.id) { onOpened?.let { pendingOpened += it }; return@post } // ya se está abriendo
        closeInternal()
        info = target
        openingId = target.id
        onOpened?.let { pendingOpened += it }
        try {
            manager.openCamera(target.id, object : CameraDevice.StateCallback() {
                override fun onOpened(cam: CameraDevice) {
                    if (openingId != target.id) { cam.close(); return }
                    device = cam
                    openingId = null
                    listener.onCameraReady(target)
                    val cbs = ArrayList(pendingOpened)
                    pendingOpened.clear()
                    cbs.forEach { it() }
                }

                override fun onDisconnected(cam: CameraDevice) {
                    Log.w(TAG, "cámara desconectada ${cam.id}")
                    cam.close()
                    if (device == cam) { device = null; session = null }
                    listener.onCameraError("Otra app tomó la cámara o el sistema la cerró", false)
                }

                override fun onError(cam: CameraDevice, error: Int) {
                    Log.e(TAG, "error de cámara $error")
                    cam.close()
                    if (device == cam) { device = null; session = null }
                    openingId = null
                    val msg = when (error) {
                        ERROR_CAMERA_IN_USE -> "La cámara está en uso por otra app"
                        ERROR_MAX_CAMERAS_IN_USE -> "Demasiadas cámaras abiertas"
                        ERROR_CAMERA_DISABLED -> "La cámara está deshabilitada por una política del sistema"
                        ERROR_CAMERA_DEVICE -> "Error del dispositivo de cámara"
                        else -> "Error del servicio de cámara"
                    }
                    listener.onCameraError(msg, error == ERROR_CAMERA_DISABLED)
                }
            }, handler)
        } catch (e: Exception) {
            openingId = null
            listener.onCameraError("No se pudo abrir la cámara ${target.id}: ${e.message}", false)
        }
    }

    fun close() = handler.post { closeInternal() }

    private fun closeInternal() {
        sessionGeneration++
        runCatching { session?.close() }
        runCatching { device?.close() }
        session = null
        device = null
        openingId = null
        pendingOpened.clear()
        surfaces = emptyList()
    }

    /** Prueba rápida: ¿se puede abrir esta cámara? (algunas marcas bloquean lentes a apps externas). */
    @SuppressLint("MissingPermission")
    fun probe(id: String, result: (Boolean) -> Unit) = handler.post {
        var done = false
        val timeout = Runnable { if (!done) { done = true; result(false) } }
        handler.postDelayed(timeout, 2500)
        try {
            manager.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(cam: CameraDevice) { cam.close(); if (!done) { done = true; handler.removeCallbacks(timeout); result(true) } }
                override fun onDisconnected(cam: CameraDevice) { cam.close(); if (!done) { done = true; result(false) } }
                override fun onError(cam: CameraDevice, error: Int) { cam.close(); if (!done) { done = true; result(false) } }
            }, handler)
        } catch (e: Exception) {
            if (!done) { done = true; handler.removeCallbacks(timeout); result(false) }
        }
    }

    // ------------------------------------------------------------- sesión

    /** (Re)crea la sesión con estas superficies. Lista vacía = cerrar sesión. */
    fun configure(requested: List<Surface>, isRecording: Boolean, onConfigured: (() -> Unit)? = null): Boolean = handler.post {
        val drop = dropPreviewFor
        val newSurfaces = if (drop != null && requested.size > 1 && requested.contains(drop)) listOf(drop) else requested
        val dev = device
        if (dev == null) { surfaces = newSurfaces; onConfigured?.invoke(); return@post }
        if (newSurfaces == surfaces && session != null && isRecording == recording) {
            onConfigured?.invoke(); return@post
        }
        val gen = ++sessionGeneration
        runCatching { session?.close() }
        session = null
        surfaces = newSurfaces
        recording = isRecording
        if (newSurfaces.isEmpty()) return@post
        val useCases = isRecording && !useCasesFailed && info?.supportsVideoUseCase == true
        val callback = object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) {
                    if (gen != sessionGeneration) { s.close(); return }
                    session = s
                    applyRepeating()
                    onConfigured?.invoke()
                }

                override fun onConfigureFailed(s: CameraCaptureSession) {
                    Log.e(TAG, "configuración de sesión falló (useCases=$useCases)")
                    if (gen != sessionGeneration) return
                    if (useCases) {
                        // Reintentar sin las optimizaciones de Android 13.
                        useCasesFailed = true
                        surfaces = emptyList()
                        configure(requested, isRecording, onConfigured)
                    } else if (isRecording && newSurfaces.size > 1) {
                        // Algunos celulares no soportan vista previa + video a esta resolución:
                        // se prioriza la transmisión.
                        dropPreviewFor = newSurfaces.last()
                        surfaces = emptyList()
                        listener.onCameraError("Este celular no soporta vista previa y video juntos a esta resolución: se transmite sin vista previa", true)
                        configure(listOf(newSurfaces.last()), true, onConfigured)
                    } else {
                        listener.onCameraError("La cámara no aceptó esta combinación de resolución/fps. Prueba otra en Ajustes.", true)
                    }
                }
            }
        try {
            if (Build.VERSION.SDK_INT >= 28) {
                val outputs = newSurfaces.mapIndexed { i, surface ->
                    OutputConfiguration(surface).also { oc ->
                        if (useCases && Build.VERSION.SDK_INT >= 33) {
                            // La última superficie es la del codificador: "grabación de video".
                            oc.streamUseCase = if (i == newSurfaces.lastIndex) CameraMetadata.SCALER_AVAILABLE_STREAM_USE_CASES_VIDEO_RECORD.toLong()
                            else CameraMetadata.SCALER_AVAILABLE_STREAM_USE_CASES_PREVIEW.toLong()
                        }
                    }
                }
                val config = SessionConfiguration(SessionConfiguration.SESSION_REGULAR, outputs, { r -> handler.post(r) }, callback)
                // Parámetros de sesión (fps, estabilización…): el HAL elige el modo del sensor adecuado desde el principio.
                buildRequest(if (isRecording) CameraDevice.TEMPLATE_RECORD else CameraDevice.TEMPLATE_PREVIEW)?.let {
                    config.sessionParameters = it.build()
                }
                dev.createCaptureSession(config)
            } else {
                @Suppress("DEPRECATION")
                dev.createCaptureSession(newSurfaces, callback, handler)
            }
        } catch (e: Exception) {
            Log.e(TAG, "createCaptureSession", e)
            listener.onCameraError("No se pudo crear la sesión de cámara: ${e.message}", false)
        }
    }

    // ------------------------------------------------------------- controles

    fun update(block: Controls.() -> Unit) = handler.post {
        controls.block()
        applyRepeating()
    }

    private fun buildRequest(template: Int): CaptureRequest.Builder? {
        val dev = device ?: return null
        val inf = info ?: return null
        val b = try { dev.createCaptureRequest(template) } catch (e: Exception) { return null }
        surfaces.forEach { b.addTarget(it) }
        val c = controls
        b.set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
        b.set(CaptureRequest.CONTROL_AE_MODE, CameraMetadata.CONTROL_AE_MODE_ON)
        inf.fpsRangeFor(c.fps)?.let { b.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) }

        // Enfoque
        if (inf.canFocus) {
            val region = focusRegion
            if (region != null) {
                b.set(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_AUTO)
                if (inf.maxAfRegions > 0) b.set(CaptureRequest.CONTROL_AF_REGIONS, arrayOf(region))
            } else {
                b.set(
                    CaptureRequest.CONTROL_AF_MODE,
                    if (inf.hasContinuousVideoAf) CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_VIDEO else CameraMetadata.CONTROL_AF_MODE_AUTO,
                )
            }
        }
        val aeRegion = focusRegion ?: if (c.meterCenter) centerRegion(inf) else null
        aeRegion?.let { r -> if (inf.maxAeRegions > 0) b.set(CaptureRequest.CONTROL_AE_REGIONS, arrayOf(r)) }
        if (inf.antibandingModes.contains(CameraMetadata.CONTROL_AE_ANTIBANDING_MODE_AUTO)) {
            b.set(CaptureRequest.CONTROL_AE_ANTIBANDING_MODE, CameraMetadata.CONTROL_AE_ANTIBANDING_MODE_AUTO)
        }

        if (inf.aeLockAvailable) b.set(CaptureRequest.CONTROL_AE_LOCK, c.aeLocked)
        if (inf.awbLockAvailable) b.set(CaptureRequest.CONTROL_AWB_LOCK, c.awbLocked)
        b.set(CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION, c.evIndex.coerceIn(inf.evRange.lower, inf.evRange.upper))
        if (inf.hasFlash) b.set(CaptureRequest.FLASH_MODE, if (c.torch) CameraMetadata.FLASH_MODE_TORCH else CameraMetadata.FLASH_MODE_OFF)

        // Estabilización: la óptica (OIS) no añade latencia; la electrónica (EIS) sí.
        if (inf.oisAvailable) b.set(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE, CameraMetadata.LENS_OPTICAL_STABILIZATION_MODE_ON)
        if (inf.eisAvailable) {
            b.set(
                CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,
                if (c.eis) CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_ON else CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_OFF,
            )
        }

        // Zoom: recorte del sensor hecho por el ISP (sin escalado digital extra de nuestra parte).
        val z = c.zoom.coerceIn(inf.minZoom, inf.maxZoom)
        if (Build.VERSION.SDK_INT >= 30 && inf.zoomRange != null) {
            b.set(CaptureRequest.CONTROL_ZOOM_RATIO, z)
        } else {
            b.set(CaptureRequest.SCALER_CROP_REGION, cropFor(inf, z))
        }
        // Procesamiento del ISP: en los modos de calidad, el de alta calidad (conserva más textura en
        // zonas oscuras); en los de baja latencia, el rápido.
        val nr = if (c.quality && inf.nrModes.contains(CameraMetadata.NOISE_REDUCTION_MODE_HIGH_QUALITY)) CameraMetadata.NOISE_REDUCTION_MODE_HIGH_QUALITY
            else CameraMetadata.NOISE_REDUCTION_MODE_FAST
        if (inf.nrModes.contains(nr)) b.set(CaptureRequest.NOISE_REDUCTION_MODE, nr)
        val edge = if (c.quality && inf.edgeModes.contains(CameraMetadata.EDGE_MODE_HIGH_QUALITY)) CameraMetadata.EDGE_MODE_HIGH_QUALITY
            else CameraMetadata.EDGE_MODE_FAST
        if (inf.edgeModes.contains(edge)) b.set(CaptureRequest.EDGE_MODE, edge)

        // Curva de tono: la del fabricante suele ser una "S" fuerte que aplasta las sombras.
        // La estándar sRGB es la referencia neutral de las pantallas: no es un filtro.
        when {
            c.neutralTone && inf.tonemapModes.contains(CameraMetadata.TONEMAP_MODE_PRESET_CURVE) -> {
                b.set(CaptureRequest.TONEMAP_MODE, CameraMetadata.TONEMAP_MODE_PRESET_CURVE)
                b.set(CaptureRequest.TONEMAP_PRESET_CURVE, CameraMetadata.TONEMAP_PRESET_CURVE_SRGB)
            }
            c.neutralTone && inf.tonemapModes.contains(CameraMetadata.TONEMAP_MODE_GAMMA_VALUE) -> {
                b.set(CaptureRequest.TONEMAP_MODE, CameraMetadata.TONEMAP_MODE_GAMMA_VALUE)
                b.set(CaptureRequest.TONEMAP_GAMMA, 2.2f)
            }
            c.quality && inf.tonemapModes.contains(CameraMetadata.TONEMAP_MODE_HIGH_QUALITY) ->
                b.set(CaptureRequest.TONEMAP_MODE, CameraMetadata.TONEMAP_MODE_HIGH_QUALITY)
        }
        return b
    }

    private fun cropFor(inf: CameraInfo, zoom: Float): Rect {
        val a = inf.activeArray
        val z = zoom.coerceAtLeast(1f)
        val w = (a.width() / z).toInt()
        val h = (a.height() / z).toInt()
        val cx = a.width() / 2
        val cy = a.height() / 2
        return Rect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2)
    }

    private fun applyRepeating() {
        val s = session ?: return
        val b = buildRequest(if (recording) CameraDevice.TEMPLATE_RECORD else CameraDevice.TEMPLATE_PREVIEW) ?: return
        try {
            s.setRepeatingRequest(b.build(), null, handler)
        } catch (e: CameraAccessException) {
            Log.w(TAG, "setRepeatingRequest", e)
        } catch (e: IllegalStateException) {
            Log.w(TAG, "sesión cerrada", e)
        } catch (e: IllegalArgumentException) {
            Log.w(TAG, "petición inválida", e)
        }
    }

    private fun trigger(afTrigger: Int) {
        val s = session ?: return
        val b = buildRequest(if (recording) CameraDevice.TEMPLATE_RECORD else CameraDevice.TEMPLATE_PREVIEW) ?: return
        b.set(CaptureRequest.CONTROL_AF_TRIGGER, afTrigger)
        runCatching { s.capture(b.build(), null, handler) }
    }

    /** Rectángulo del sensor que corresponde al cuadro 16:9 que se transmite (ya con el zoom). */
    private fun frameRect(inf: CameraInfo): Rect {
        val base = if (Build.VERSION.SDK_INT >= 30 && inf.zoomRange != null) {
            Rect(0, 0, inf.activeArray.width(), inf.activeArray.height())
        } else {
            cropFor(inf, controls.zoom)
        }
        val aspect = base.width().toFloat() / base.height()
        return if (aspect > 16f / 9f) {
            val w = (base.height() * 16f / 9f).toInt()
            Rect(base.centerX() - w / 2, base.top, base.centerX() + w / 2, base.bottom)
        } else {
            val h = (base.width() * 9f / 16f).toInt()
            Rect(base.left, base.centerY() - h / 2, base.right, base.centerY() + h / 2)
        }
    }

    /** Medición centrada: la mitad central del cuadro (donde suele estar quien habla). */
    private fun centerRegion(inf: CameraInfo): MeteringRectangle {
        val r = frameRect(inf)
        val rect = Rect(
            r.left + r.width() / 4, r.top + r.height() / 5,
            r.right - r.width() / 4, r.bottom - r.height() / 5,
        )
        return MeteringRectangle(rect, MeteringRectangle.METERING_WEIGHT_MAX - 1)
    }

    /**
     * Toque para enfocar. (nx, ny) normalizados en el cuadro 16:9 que se transmite.
     * Vuelve a enfoque continuo a los 5 s salvo que el enfoque esté bloqueado.
     */
    fun focusAt(nx: Float, ny: Float) = handler.post {
        val inf = info ?: return@post
        if (!inf.canFocus && inf.maxAeRegions == 0) return@post
        // El flujo 16:9 es un recorte centrado de la región del sensor.
        val r = frameRect(inf)
        val x = (r.left + nx.coerceIn(0f, 1f) * r.width()).toInt()
        val y = (r.top + ny.coerceIn(0f, 1f) * r.height()).toInt()
        val half = (r.width() * 0.06f).toInt().coerceAtLeast(16)
        val left = (x - half).coerceIn(0, inf.activeArray.width() - 1)
        val top = (y - half).coerceIn(0, inf.activeArray.height() - 1)
        val right = (x + half).coerceIn(left + 1, inf.activeArray.width())
        val bottom = (y + half).coerceIn(top + 1, inf.activeArray.height())
        focusRegion = MeteringRectangle(Rect(left, top, right, bottom), MeteringRectangle.METERING_WEIGHT_MAX - 1)
        applyRepeating()
        if (inf.canFocus) {
            trigger(CameraMetadata.CONTROL_AF_TRIGGER_CANCEL)
            trigger(CameraMetadata.CONTROL_AF_TRIGGER_START)
        }
        val token = ++focusResetToken
        handler.postDelayed({
            if (token == focusResetToken && !controls.afLocked) {
                focusRegion = null
                applyRepeating()
            }
        }, 5000)
    }

    /** Bloquea/desbloquea el enfoque en la posición actual. */
    fun setAfLocked(locked: Boolean) = handler.post {
        controls.afLocked = locked
        val inf = info ?: return@post
        if (!inf.canFocus) return@post
        if (locked) {
            focusResetToken++
            trigger(CameraMetadata.CONTROL_AF_TRIGGER_START)
        } else {
            focusRegion = null
            applyRepeating()
            trigger(CameraMetadata.CONTROL_AF_TRIGGER_CANCEL)
        }
    }

    fun release() {
        handler.post { closeInternal() }
        thread.quitSafely()
    }

    fun nowMs() = SystemClock.elapsedRealtime()
}
