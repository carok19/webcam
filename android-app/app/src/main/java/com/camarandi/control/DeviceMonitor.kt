package com.camarandi.control

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.PowerManager

/**
 * Temperatura, batería y WiFi. "level": 0 = verde, 1 = amarillo, 2 = rojo.
 * Usa el estado térmico del sistema (Android 10+), el margen térmico previsto
 * (Android 11+) y la temperatura de la batería.
 */
class DeviceMonitor(private val context: Context) {

    data class Status(
        val level: Int,
        val thermalStatus: Int,
        val headroom: Float,
        val batteryTempC: Float,
        val batteryPct: Int,
        val charging: Boolean,
        val wifiMhz: Int,
        val wifiRssi: Int,
        /** CPU usada por esta app (no incluye el proceso de cámara ni el codificador por hardware). */
        val appCpuPct: Double,
    ) {
        val levelName get() = when (level) { 2 -> "hot"; 1 -> "warm"; else -> "ok" }
        val wifiBand get() = when {
            wifiMhz <= 0 -> "?"
            wifiMhz < 3000 -> "2.4 GHz"
            wifiMhz < 5925 -> "5 GHz"
            else -> "6 GHz"
        }
    }

    private val power = context.getSystemService(Context.POWER_SERVICE) as PowerManager
    private val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    private var lastHeadroomMs = 0L
    private var lastHeadroom = Float.NaN
    private var lastCpuMs = android.os.Process.getElapsedCpuTime()
    private var lastWallMs = android.os.SystemClock.elapsedRealtime()
    private var cpuPct = 0.0
    private val cores = Runtime.getRuntime().availableProcessors().coerceAtLeast(1)

    fun read(): Status {
        val bat = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val temp = (bat?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) ?: 0) / 10f
        val lvl = bat?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
        val scale = bat?.getIntExtra(BatteryManager.EXTRA_SCALE, 100) ?: 100
        val pct = if (lvl >= 0 && scale > 0) lvl * 100 / scale else -1
        val plugged = (bat?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0

        val thermal = if (Build.VERSION.SDK_INT >= 29) power.currentThermalStatus else 0
        val now = android.os.SystemClock.elapsedRealtime()
        if (Build.VERSION.SDK_INT >= 30 && now - lastHeadroomMs >= 2000) {
            lastHeadroomMs = now
            lastHeadroom = runCatching { power.getThermalHeadroom(10) }.getOrDefault(Float.NaN)
        }
        val head = lastHeadroom

        val level = when {
            thermal >= PowerManager.THERMAL_STATUS_SEVERE || temp >= 45f || (!head.isNaN() && head >= 0.95f) -> 2
            thermal >= PowerManager.THERMAL_STATUS_MODERATE || temp >= 41f || (!head.isNaN() && head >= 0.8f) -> 1
            else -> 0
        }

        val cpuNow = android.os.Process.getElapsedCpuTime()
        if (now - lastWallMs >= 900) {
            cpuPct = 100.0 * (cpuNow - lastCpuMs) / ((now - lastWallMs) * cores)
            lastCpuMs = cpuNow
            lastWallMs = now
        }

        @Suppress("DEPRECATION")
        val info = runCatching { wifi.connectionInfo }.getOrNull()
        return Status(level, thermal, head, temp, pct, plugged, info?.frequency ?: 0, info?.rssi ?: 0, cpuPct)
    }
}
