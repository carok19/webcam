package com.camarandi.camera

import android.graphics.Rect
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.util.Range
import android.util.Rational
import android.util.Size
import com.camarandi.encoder.EncoderCatalog
import java.util.Locale

/** Resoluciones/fps que ofrece la app (de mayor a menor). */
data class Preset(val id: String, val width: Int, val height: Int, val fps: Int) {
    val shortRes: String get() = when (height) { 2160 -> "4K"; else -> "${height}p" }
    val label: String get() = "$shortRes$fps"
    val pixelRate: Long get() = width.toLong() * height * fps

    companion object {
        val ALL = listOf(
            Preset("2160p30", 3840, 2160, 30),
            Preset("1440p30", 2560, 1440, 30),
            Preset("1080p60", 1920, 1080, 60),
            Preset("1080p30", 1920, 1080, 30),
            Preset("720p60", 1280, 720, 60),
            Preset("720p30", 1280, 720, 30),
        )
        fun byId(id: String?): Preset? = ALL.firstOrNull { it.id == id }
    }
}

data class PresetSupport(
    val preset: Preset,
    val ok: Boolean,
    val reason: String,
    val hevc: Boolean,
    val h264: Boolean,
)

/** Un "lente" que el usuario puede elegir con los chips (0.5x, 1x, 2x…). */
data class Lens(
    val key: String,
    val cameraId: String,
    /** Si no es null, este lente se alcanza poniendo este zoomRatio en la cámara lógica. */
    val zoomRatio: Float?,
    /** Campo de visión relativo al principal (0.5 = ultra gran angular, 2 = telefoto 2x). */
    val factor: Float,
    val kind: String,
) {
    val label: String get() = formatFactor(factor)
}

fun formatFactor(f: Float): String {
    val r = Math.round(f * 10) / 10f
    return if (r >= 1f && Math.abs(r - Math.round(r)) < 0.05f) "${Math.round(r)}x"
    else String.format(Locale.US, "%.1fx", r)
}

/** Datos de una cámara (lógica o simple) que la app necesita. */
class CameraInfo(val id: String, val chars: CameraCharacteristics) {
    val facing: Int = chars[CameraCharacteristics.LENS_FACING] ?: CameraMetadata.LENS_FACING_BACK
    val isFront get() = facing == CameraMetadata.LENS_FACING_FRONT
    val isBack get() = facing == CameraMetadata.LENS_FACING_BACK
    val capabilities: IntArray = chars[CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES] ?: IntArray(0)
    val backwardCompatible = capabilities.contains(CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_BACKWARD_COMPATIBLE)
    val logical = Build.VERSION.SDK_INT >= 28 &&
        capabilities.contains(CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_LOGICAL_MULTI_CAMERA)
    val physicalIds: Set<String> = if (Build.VERSION.SDK_INT >= 28 && logical) chars.physicalCameraIds else emptySet()
    val focalMm: Float = chars[CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS]?.minOrNull() ?: 4f
    val sensorWidthMm: Float = chars[CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE]?.width ?: 5f
    /** Métrica de "acercamiento": distancia focal / ancho del sensor (mayor = más zoom). */
    val fovMetric: Float = focalMm / sensorWidthMm
    val activeArray: Rect = chars[CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE] ?: Rect(0, 0, 4000, 3000)
    val sensorOrientation: Int = chars[CameraCharacteristics.SENSOR_ORIENTATION] ?: 90
    val hardwareLevel: Int = chars[CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL] ?: CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_LEGACY
    val timestampRealtime: Boolean = chars[CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE] ==
        CameraMetadata.SENSOR_INFO_TIMESTAMP_SOURCE_REALTIME
    val zoomRange: Range<Float>? = if (Build.VERSION.SDK_INT >= 30) chars[CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE] else null
    val maxDigitalZoom: Float = chars[CameraCharacteristics.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM] ?: 1f
    val minZoom: Float get() = zoomRange?.lower ?: 1f
    val maxZoom: Float get() = zoomRange?.upper ?: maxDigitalZoom
    val fpsRanges: Array<Range<Int>> = chars[CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES] ?: emptyArray()
    val hasFlash: Boolean = chars[CameraCharacteristics.FLASH_INFO_AVAILABLE] == true
    val afModes: IntArray = chars[CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES] ?: IntArray(0)
    val minFocusDistance: Float = chars[CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE] ?: 0f
    val canFocus get() = minFocusDistance > 0f && afModes.contains(CameraMetadata.CONTROL_AF_MODE_AUTO)
    val hasContinuousVideoAf get() = afModes.contains(CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
    val aeLockAvailable: Boolean = chars[CameraCharacteristics.CONTROL_AE_LOCK_AVAILABLE] == true
    val awbLockAvailable: Boolean = chars[CameraCharacteristics.CONTROL_AWB_LOCK_AVAILABLE] == true
    val evRange: Range<Int> = chars[CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE] ?: Range(0, 0)
    val evStep: Rational = chars[CameraCharacteristics.CONTROL_AE_COMPENSATION_STEP] ?: Rational(1, 3)
    val oisAvailable: Boolean = (chars[CameraCharacteristics.LENS_INFO_AVAILABLE_OPTICAL_STABILIZATION] ?: IntArray(0))
        .contains(CameraMetadata.LENS_OPTICAL_STABILIZATION_MODE_ON)
    val eisAvailable: Boolean = (chars[CameraCharacteristics.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES] ?: IntArray(0))
        .contains(CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_ON)
    val maxAfRegions: Int = chars[CameraCharacteristics.CONTROL_MAX_REGIONS_AF] ?: 0
    val maxAeRegions: Int = chars[CameraCharacteristics.CONTROL_MAX_REGIONS_AE] ?: 0

    private val streamMap = chars[CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP]

    fun encoderSizes(): List<Size> = streamMap?.getOutputSizes(MediaCodec::class.java)?.toList() ?: emptyList()

    fun previewSize(): Size {
        val sizes = streamMap?.getOutputSizes(android.view.SurfaceHolder::class.java)?.toList() ?: emptyList()
        val wanted = Size(1280, 720)
        if (sizes.contains(wanted)) return wanted
        return sizes.filter { it.width * 9 == it.height * 16 && it.width <= 1920 }.maxByOrNull { it.width } ?: wanted
    }

    fun maxFpsFor(size: Size): Double {
        val d = runCatching { streamMap?.getOutputMinFrameDuration(MediaCodec::class.java, size) ?: 0L }.getOrDefault(0L)
        return if (d > 0) 1e9 / d else 30.0
    }

    /** El mejor rango de fps para [fps]: fijo [fps,fps] si existe (más fluido). */
    fun fpsRangeFor(fps: Int): Range<Int>? {
        fpsRanges.firstOrNull { it.lower == fps && it.upper == fps }?.let { return it }
        return fpsRanges.filter { it.upper == fps }.maxByOrNull { it.lower }
    }

    /** fps disponibles menores a [fps] (para bajar fps por temperatura/red). */
    fun lowerFpsSteps(fps: Int): List<Int> =
        listOf(30, 25, 24, 20, 15).filter { it < fps && fpsRangeFor(it) != null }

    fun evaluate(p: Preset): PresetSupport {
        val size = Size(p.width, p.height)
        if (!encoderSizes().contains(size)) return PresetSupport(p, false, "la cámara no entrega ${p.width}x${p.height}", false, false)
        val camFps = maxFpsFor(size)
        if (camFps < p.fps * 0.98) return PresetSupport(p, false, "la cámara llega a ${camFps.toInt()} fps en ${p.shortRes}", false, false)
        if (fpsRangeFor(p.fps) == null) return PresetSupport(p, false, "la cámara no ofrece ${p.fps} fps", false, false)
        val hevc = EncoderCatalog.hardwareEncoder(MediaFormat.MIMETYPE_VIDEO_HEVC)?.sustains(p.width, p.height, p.fps)
        val avc = EncoderCatalog.hardwareEncoder(MediaFormat.MIMETYPE_VIDEO_AVC)?.sustains(p.width, p.height, p.fps)
        val okH = hevc?.first == true
        val okA = avc?.first == true
        if (!okH && !okA) {
            val why = avc?.second?.takeIf { it.isNotEmpty() } ?: hevc?.second ?: "no hay codificador por hardware"
            return PresetSupport(p, false, why, false, false)
        }
        return PresetSupport(p, true, "", okH, okA)
    }

    val hardwareLevelName: String
        get() = when (hardwareLevel) {
            CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_LEGACY -> "LEGACY"
            CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_LIMITED -> "LIMITED"
            CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_FULL -> "FULL"
            CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_3 -> "LEVEL_3"
            else -> "EXTERNAL"
        }
}

/** Todas las cámaras/lentes utilizables del celular. */
class CameraCatalog(private val manager: CameraManager, blockedIds: Set<String>) {
    val cameras: List<CameraInfo>
    val mainBack: CameraInfo?
    val mainFront: CameraInfo?
    val backLenses: List<Lens>
    /** Factores ópticos alcanzables por zoomRatio en la cámara principal trasera. */
    val mainOpticalFactors: List<Float>
    /** Cámaras traseras extra que conviene probar si abren (pueden estar bloqueadas por la marca). */
    val probeCandidates: List<String>
    val notes = ArrayList<String>()

    init {
        val list = ArrayList<CameraInfo>()
        for (id in runCatching { manager.cameraIdList.toList() }.getOrDefault(emptyList())) {
            val info = runCatching { CameraInfo(id, manager.getCameraCharacteristics(id)) }.getOrNull() ?: continue
            if (!info.backwardCompatible) { notes += "Cámara $id ignorada (no es de video/color)"; continue }
            list += info
        }
        cameras = list
        mainBack = list.firstOrNull { it.isBack }
        mainFront = list.firstOrNull { it.isFront }

        val lenses = ArrayList<Lens>()
        val optical = ArrayList<Float>()
        val probes = ArrayList<String>()
        val main = mainBack
        if (main != null) {
            val mainMetric = main.fovMetric
            optical += 1f
            lenses += Lens("main", main.id, 1f, 1f, "principal")
            if (main.minZoom < 0.95f) {
                lenses += Lens("uw-zoom", main.id, main.minZoom, main.minZoom, "ultra gran angular")
                optical += main.minZoom
            }
            // Telefoto dentro de la cámara lógica (el HAL cambia de lente solo al hacer zoom).
            for (pid in main.physicalIds) {
                val pc = runCatching { CameraInfo(pid, manager.getCameraCharacteristics(pid)) }.getOrNull() ?: continue
                val f = pc.fovMetric / mainMetric
                if (f >= 1.8f && f <= main.maxZoom + 0.01f) {
                    lenses += Lens("tele-$pid", main.id, f, f, "telefoto")
                    optical += f
                } else if (f < 0.8f && main.minZoom >= 0.95f) {
                    notes += "Ultra gran angular (cámara física $pid) no accesible por zoom en este celular"
                }
            }
            // Otras cámaras traseras expuestas por separado.
            for (c in list) {
                if (c === main || !c.isBack || c.id in blockedIds) continue
                if (c.encoderSizes().none { it.width >= 1280 }) { notes += "Cámara ${c.id} descartada (resolución baja)"; continue }
                val f = c.fovMetric / mainMetric
                if (lenses.any { Math.abs(it.factor - f) / f < 0.12f }) continue
                val kind = when {
                    f < 0.8f -> "ultra gran angular"
                    f >= 1.8f -> "telefoto"
                    else -> "secundaria"
                }
                lenses += Lens("cam-${c.id}", c.id, null, f, kind)
                probes += c.id
            }
        }
        backLenses = lenses.sortedBy { it.factor }
        mainOpticalFactors = optical.sorted()
        probeCandidates = probes
        if (lenses.none { it.kind == "telefoto" }) notes += "Este celular no expone un lente telefoto: el zoom es por recorte del sensor"
    }

    fun byId(id: String): CameraInfo? = cameras.firstOrNull { it.id == id }
}
