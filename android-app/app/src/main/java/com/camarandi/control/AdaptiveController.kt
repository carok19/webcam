package com.camarandi.control

import com.camarandi.net.Protocol

/**
 * Bitrate adaptativo. Ante congestión: primero baja bitrate, después fps y al final resolución.
 * Sube despacio cuando la red se estabiliza. Prioriza fluidez sobre nitidez.
 * La temperatura pone un techo extra (no corta la transmisión).
 */
class AdaptiveController(
    var maxKbps: Int,
    private val listener: Listener,
) {
    interface Listener {
        fun applyBitrate(kbps: Int)
        /** 0 = fps original, 1+ = un escalón menos por nivel. */
        fun applyFpsLevel(level: Int)
        /** 0 = resolución elegida, 1+ = un escalón menos por nivel. */
        fun applyResolutionLevel(level: Int)
        fun onAdaptMessage(msg: String)
    }

    var maxFpsLevel = 1
    var maxResLevel = 0
    /**
     * Modos de calidad: el buffer del receptor ya absorbe las pérdidas, así que sólo se baja el
     * bitrate si de verdad se pierden frames, y nunca se bajan fps ni resolución por la red.
     */
    var qualityMode = false

    var bitrateKbps = (maxKbps * 0.7).toInt()
        private set
    var fpsLevel = 0
        private set
    var resLevel = 0
        private set
    var thermalLevel = 0
        private set

    private var cleanMs = 0L
    private var congestedAtFloorMs = 0L
    private var lastIncreaseMs = 0L

    val floorKbps: Int get() = maxOf(800, if (qualityMode) maxKbps / 2 else maxKbps / 5)

    private fun ceilingKbps(): Int {
        val thermalFactor = when (thermalLevel) { 2 -> 0.5; 1 -> 0.75; else -> 1.0 }
        val resFactor = Math.pow(0.6, resLevel.toDouble())
        return maxOf(floorKbps, (maxKbps * thermalFactor * resFactor).toInt())
    }

    fun reset(newMaxKbps: Int) {
        maxKbps = newMaxKbps
        bitrateKbps = (maxKbps * if (qualityMode) 0.85 else 0.7).toInt()
        fpsLevel = 0
        resLevel = 0
        cleanMs = 0
        congestedAtFloorMs = 0
        listener.applyBitrate(minOf(bitrateKbps, ceilingKbps()))
    }

    /** Nuevo estado térmico (0 verde, 1 amarillo, 2 rojo). */
    fun onThermal(level: Int) {
        if (level == thermalLevel) return
        val prev = thermalLevel
        thermalLevel = level
        val ceil = ceilingKbps()
        if (bitrateKbps > ceil) setBitrate(ceil)
        // En rojo también bajamos fps (si se puede).
        val wantFps = if (level >= 2) 1 else 0
        if (wantFps > fpsLevel && fpsLevel < maxFpsLevel) setFps(wantFps)
        if (level > prev) {
            listener.onAdaptMessage(
                if (level >= 2) "Celular muy caliente: bajando bitrate y fps (la transmisión sigue)"
                else "Celular calentándose: bajando bitrate"
            )
        }
    }

    fun onReport(r: Protocol.Report, rttMs: Double, minRttMs: Double) {
        val interval = r.intervalMs.toLong().coerceIn(100, 2000)
        if (r.expected < 10) return
        val loss = r.missing.toDouble() / r.expected
        val lost = r.lost.toDouble() / r.expected
        val rttUp = minRttMs > 0 && rttMs > minRttMs + 40
        val suggestedLower = r.suggestedKbps in 1 until bitrateKbps
        val congested = if (qualityMode) {
            loss > 0.10 || lost > 0.02 || r.framesDropped > 2 || suggestedLower
        } else {
            loss > 0.04 || lost > 0.01 || r.framesDropped > 0 || rttUp || suggestedLower
        }
        val clean = if (qualityMode) {
            loss < 0.03 && r.framesDropped == 0L
        } else {
            loss < 0.01 && r.framesDropped == 0L && !(minRttMs > 0 && rttMs > minRttMs + 20)
        }

        if (congested) {
            cleanMs = 0
            var target = (bitrateKbps * if (qualityMode) 0.85 else 0.75).toInt()
            if (suggestedLower) target = minOf(target, r.suggestedKbps.toInt())
            target = maxOf(floorKbps, target)
            if (target < bitrateKbps) setBitrate(target)
            if (bitrateKbps <= floorKbps) {
                congestedAtFloorMs += interval
                if (!qualityMode && congestedAtFloorMs >= 2000 && fpsLevel < maxFpsLevel) {
                    setFps(fpsLevel + 1)
                    listener.onAdaptMessage("Red saturada: bajando fps para mantener la fluidez")
                } else if (!qualityMode && congestedAtFloorMs >= 6000 && resLevel < maxResLevel) {
                    resLevel++
                    congestedAtFloorMs = 2000
                    listener.applyResolutionLevel(resLevel)
                    listener.onAdaptMessage("Red muy saturada: bajando resolución")
                }
            }
        } else if (clean) {
            congestedAtFloorMs = 0
            cleanMs += interval
            val ceil = ceilingKbps()
            val now = android.os.SystemClock.elapsedRealtime()
            if (cleanMs >= 5000 && bitrateKbps < ceil && now - lastIncreaseMs >= 1000) {
                lastIncreaseMs = now
                setBitrate(minOf(ceil, (bitrateKbps * 1.08).toInt() + 50))
            }
            val wantFpsFloor = if (thermalLevel >= 2) 1 else 0
            if (cleanMs >= 10_000 && bitrateKbps >= ceil * 0.95 && fpsLevel > wantFpsFloor) {
                setFps(fpsLevel - 1)
                cleanMs = 5000
            } else if (cleanMs >= 20_000 && fpsLevel == 0 && resLevel > 0 && thermalLevel == 0) {
                resLevel--
                cleanMs = 5000
                listener.applyResolutionLevel(resLevel)
            }
        }
    }

    private fun setBitrate(kbps: Int) {
        bitrateKbps = kbps
        listener.applyBitrate(kbps)
    }

    private fun setFps(level: Int) {
        fpsLevel = level.coerceIn(0, maxFpsLevel)
        listener.applyFpsLevel(fpsLevel)
    }
}
