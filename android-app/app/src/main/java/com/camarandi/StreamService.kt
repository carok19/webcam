package com.camarandi

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.PowerManager

/**
 * Servicio en primer plano: mantiene cámara + codificador + red funcionando con la pantalla
 * apagada. Muestra una notificación permanente con botón "Detener".
 */
class StreamService : Service() {

    inner class LocalBinder : Binder() {
        val service: StreamService get() = this@StreamService
    }

    companion object {
        const val ACTION_START = "com.camarandi.START"
        const val ACTION_STOP = "com.camarandi.STOP"
        private const val CHANNEL = "transmision"
        private const val NOTIF_ID = 1
    }

    private val binder = LocalBinder()
    lateinit var engine: StreamEngine
        private set
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var wifiLockLowLatency: WifiManager.WifiLock? = null
    private var foreground = false

    override fun onCreate() {
        super.onCreate()
        engine = StreamEngine(this)
        engine.serviceListener = { streaming -> if (streaming) goForeground() else leaveForeground() }
        createChannel()
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                foreground = false // startForeground() debe llamarse siempre tras startForegroundService()
                goForeground()
                engine.startStreaming()
            }
            ACTION_STOP -> if (engine.streaming) engine.stopStreaming() else if (!foreground) stopSelf()
        }
        return START_NOT_STICKY
    }

    private fun goForeground() {
        if (foreground) return
        val wasLocked = wakeLock != null
        foreground = true
        val n = buildNotification()
        if (Build.VERSION.SDK_INT >= 30) {
            startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA)
        } else {
            startForeground(NOTIF_ID, n)
        }
        if (!wasLocked) acquireLocks()
    }

    private fun leaveForeground() {
        if (!foreground) return
        foreground = false
        releaseLocks()
        if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE) else @Suppress("DEPRECATION") stopForeground(true)
        stopSelf() // sigue vivo mientras la actividad esté enlazada
    }

    private fun acquireLocks() {
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "camarandi:transmision").apply {
            setReferenceCounted(false)
            acquire(12 * 60 * 60 * 1000L)
        }
        val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        @Suppress("DEPRECATION")
        wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "camarandi:wifi").apply {
            setReferenceCounted(false)
            acquire()
        }
        if (Build.VERSION.SDK_INT >= 29) {
            wifiLockLowLatency = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "camarandi:wifi-ll").apply {
                setReferenceCounted(false)
                acquire()
            }
        }
    }

    private fun releaseLocks() {
        runCatching { wakeLock?.release() }
        runCatching { wifiLock?.release() }
        runCatching { wifiLockLowLatency?.release() }
        wakeLock = null
        wifiLock = null
        wifiLockLowLatency = null
    }

    private fun createChannel() {
        val nm = getSystemService(NotificationManager::class.java)
        val ch = NotificationChannel(CHANNEL, "Transmisión", NotificationManager.IMPORTANCE_LOW).apply {
            description = "Aviso permanente mientras el celular transmite"
            setShowBadge(false)
        }
        nm.createNotificationChannel(ch)
    }

    private fun buildNotification(): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, StreamService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        return Notification.Builder(this, CHANNEL)
            .setContentTitle("Cámara NDI transmitiendo")
            .setContentText("${engine.settings.deviceName}: la cámara sigue activa aunque apagues la pantalla")
            .setSmallIcon(R.drawable.ic_notif)
            .setOngoing(true)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Detener", stop).build())
            .build()
    }

    override fun onDestroy() {
        releaseLocks()
        engine.release()
        super.onDestroy()
    }
}
