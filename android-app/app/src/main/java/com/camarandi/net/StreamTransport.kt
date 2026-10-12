package com.camarandi.net

import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.SystemClock
import android.util.Log
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.Random

/**
 * Transporte UDP hacia el receptor:
 *  - descubrimiento por broadcast (o IP escrita a mano),
 *  - HELLO cada 1 s (estado + keep-alive), PING 4 veces por segundo (RTT),
 *  - video troceado en paquetes de ≤1200 bytes con número de secuencia,
 *  - búfer circular de los últimos paquetes para reenviarlos cuando el receptor manda NACK,
 *  - reconexión automática sin tocar la cámara.
 *
 * Nunca hay cola: si no estamos conectados, los frames simplemente se descartan.
 */
class StreamTransport(
    private val connectivity: ConnectivityManager,
    private val listener: Listener,
) {
    interface Listener {
        fun onStateChanged(state: State, detail: String)
        fun onConnected(receiverName: String)
        fun onKeyframeRequested()
        fun onReport(report: Protocol.Report, rttMs: Double, minRttMs: Double)
        fun onRemoteDisconnect(reason: String)
        /**
         * Qué decodifica la GPU del PC (alturas máximas; 0 = no puede, -1 = no se sabe) y el límite
         * de altura que pide el receptor porque no alcanzó (0 = sin límite).
         */
        /** maxHeight/maxPixelRate: límite que pide el PC porque no alcanzó a decodificar (0 = sin límite). */
        fun onReceiverDecodeCaps(hevcGpuMaxHeight: Int, h264GpuMaxHeight: Int, maxHeight: Int, maxPixelRate: Long, gpu: String)
        /** El PC no alcanza: elegir altura ≤ maxHeight y ancho×alto×fps ≤ maxPixelRate (los dos en 0 = se quitó el límite). */
        fun onReceiverLimit(maxHeight: Int, maxPixelRate: Long, reason: String)
        /** JSON del HELLO (se pide cada segundo). */
        fun buildHello(): JSONObject
    }

    enum class State { DETENIDO, BUSCANDO, CONECTANDO, CONECTADO, RECONECTANDO, RECHAZADO }

    companion object {
        private const val TAG = "Transporte"
        private const val RING = 4096
        private const val TIMEOUT_MS = 3000L
    }

    val sessionId: Int = Random().nextInt(Int.MAX_VALUE - 1) + 1

    @Volatile var state = State.DETENIDO
        private set
    @Volatile var receiverName: String = ""
        private set
    @Volatile var target: InetSocketAddress? = null
        private set
    @Volatile var rttMs = 0.0
        private set
    @Volatile var minRttMs = Double.MAX_VALUE
        private set
    @Volatile var sentKbps = 0.0
        private set
    @Volatile var retransmitted = 0L
        private set

    /** IP escrita a mano (vacío = descubrimiento automático). */
    @Volatile var manualHost: String = ""
    /** Última IP conocida (se prueba directamente mientras se busca). */
    @Volatile var lastKnownHost: String = ""

    private var socket: DatagramSocket? = null
    private var discoverySocket: DatagramSocket? = null
    @Volatile private var running = false
    private var controlThread: Thread? = null
    private var rxThread: Thread? = null

    private val ring = Array(RING) { ByteArray(Protocol.MAX_PACKET) }
    private val ringLen = IntArray(RING)
    private val ringSeq = IntArray(RING) { -1 }
    private val ringLock = Any()
    private var seq = 0
    private var frameId = 0
    private val sendPacket = DatagramPacket(ByteArray(0), 0)

    @Volatile private var lastRxMs = 0L
    @Volatile private var acked = false
    private var bytesWindow = 0L
    private var bytesWindowStart = 0L

    fun start() {
        if (running) return
        running = true
        lastRxMs = 0
        acked = false
        try {
            socket = DatagramSocket(null).apply {
                reuseAddress = true
                bind(InetSocketAddress(0))
                sendBufferSize = 2 * 1024 * 1024
                receiveBufferSize = 512 * 1024
                trafficClass = 0xA0 // DSCP CS5 -> cola de video (WMM AC_VI) en el WiFi
                soTimeout = 500
            }
            bindToWifi(socket!!)
        } catch (e: Exception) {
            Log.e(TAG, "No se pudo abrir el socket", e)
            setState(State.DETENIDO, "Error de red: ${e.message}")
            running = false
            return
        }
        rxThread = Thread({ receiveLoop() }, "red-rx").apply { priority = Thread.MAX_PRIORITY; start() }
        controlThread = Thread({ controlLoop() }, "red-control").apply { start() }
    }

    fun stop() {
        if (!running) return
        running = false
        target?.let { t -> runCatching { send(Protocol.buildBye(sessionId), t) } }
        runCatching { socket?.close() }
        runCatching { discoverySocket?.close() }
        controlThread?.join(1000)
        rxThread?.join(1000)
        socket = null
        discoverySocket = null
        setState(State.DETENIDO, "")
    }

    val isConnected: Boolean get() = state == State.CONECTADO

    /** Olvidar el destino actual (p. ej. cambió la IP escrita a mano) y volver a buscar/conectar. */
    fun retarget() {
        target = null
        acked = false
        if (running) setState(State.BUSCANDO, "Buscando receptor…")
    }

    /**
     * Trocea y envía un frame codificado. Llamado desde el hilo del codificador, sin colas.
     * @return false si se descartó por no estar conectados.
     */
    fun sendFrame(data: ByteArray, length: Int, ptsUs: Long, keyframe: Boolean, codec: Int): Boolean {
        val t = target
        val s = socket
        if (state != State.CONECTADO || t == null || s == null) return false
        val count = (length + Protocol.MAX_PAYLOAD - 1) / Protocol.MAX_PAYLOAD
        if (count > 65535) return false
        val fid = frameId++
        val flags = if (keyframe) Protocol.FLAG_KEYFRAME else 0
        var off = 0
        for (i in 0 until count) {
            val n = minOf(Protocol.MAX_PAYLOAD, length - off)
            val sq: Int
            val slot: Int
            synchronized(ringLock) {
                sq = seq++
                slot = (sq and 0x7FFFFFFF) % RING
                val buf = ring[slot]
                Protocol.writeVideoHeader(buf, sessionId, sq, fid, i, count, flags, codec, ptsUs)
                System.arraycopy(data, off, buf, Protocol.VIDEO_HEADER_SIZE, n)
                ringLen[slot] = Protocol.VIDEO_HEADER_SIZE + n
                ringSeq[slot] = sq
                sendRaw(ring[slot], ringLen[slot], t)
            }
            off += n
        }
        return true
    }

    private fun sendRaw(buf: ByteArray, len: Int, to: InetSocketAddress) {
        try {
            synchronized(sendPacket) {
                sendPacket.setData(buf, 0, len)
                sendPacket.socketAddress = to
                socket?.send(sendPacket)
            }
            countBytes(len)
        } catch (e: Exception) {
            // La red puede caerse un momento: el control lo detecta y reconecta.
        }
    }

    private fun send(data: ByteArray, to: InetSocketAddress) = sendRaw(data, data.size, to)

    private fun countBytes(n: Int) {
        val now = SystemClock.elapsedRealtime()
        synchronized(this) {
            if (bytesWindowStart == 0L) bytesWindowStart = now
            bytesWindow += n
            val dt = now - bytesWindowStart
            if (dt >= 1000) {
                sentKbps = bytesWindow * 8.0 / dt
                bytesWindow = 0
                bytesWindowStart = now
            }
        }
    }

    // ------------------------------------------------------------------ control

    private fun controlLoop() {
        var lastHello = 0L
        var lastPing = 0L
        var lastDiscover = 0L
        var pingId = 0
        var reconnectingSince = 0L
        while (running) {
            val now = SystemClock.elapsedRealtime()
            try {
                if (target == null) {
                    val manual = manualHost.trim()
                    if (manual.isNotEmpty()) {
                        target = resolve(manual)
                        if (target == null) setState(State.BUSCANDO, "IP no válida: $manual")
                        else setState(State.CONECTANDO, "Conectando a $manual…")
                    } else {
                        if (state != State.BUSCANDO && state != State.RECONECTANDO) setState(State.BUSCANDO, "Buscando receptor en la red…")
                        if (now - lastDiscover >= 700) {
                            lastDiscover = now
                            discover()?.let { found ->
                                target = found
                                lastKnownHost = found.address.hostAddress ?: ""
                                setState(State.CONECTANDO, "Receptor encontrado en ${found.address.hostAddress}")
                            }
                        }
                        // Mientras tanto, probar la última IP conocida.
                        if (target == null && lastKnownHost.isNotEmpty() && now - lastHello >= 1000) {
                            resolve(lastKnownHost)?.let { send(buildHello(), it); lastHello = now }
                        }
                    }
                }

                val t = target
                if (t != null) {
                    if (now - lastHello >= 1000) {
                        lastHello = now
                        send(buildHello(), t)
                    }
                    if (now - lastPing >= 250) {
                        lastPing = now
                        send(Protocol.buildPing(sessionId, pingId++, SystemClock.elapsedRealtimeNanos() / 1000), t)
                    }
                }

                // Vigilancia de la conexión.
                val silent = lastRxMs == 0L || now - lastRxMs > TIMEOUT_MS
                if (state == State.CONECTADO && silent) {
                    acked = false
                    reconnectingSince = now
                    setState(State.RECONECTANDO, "Sin respuesta del receptor. Reconectando…")
                }
                if (state == State.RECONECTANDO && manualHost.isBlank() && now - reconnectingSince > 4000) {
                    // Quizá el PC cambió de IP: volver a buscar (la cámara sigue abierta).
                    target = null
                    reconnectingSince = now
                }
            } catch (e: Exception) {
                Log.w(TAG, "control", e)
            }
            try { Thread.sleep(50) } catch (_: InterruptedException) { break }
        }
    }

    private fun buildHello(): ByteArray {
        val json = try { listener.buildHello() } catch (e: Exception) { JSONObject() }
        return Protocol.buildJson(Protocol.HELLO, sessionId, json.toString())
    }

    private fun resolve(host: String): InetSocketAddress? = try {
        val parts = host.split(":")
        val port = parts.getOrNull(1)?.toIntOrNull() ?: Protocol.MEDIA_PORT
        InetSocketAddress(InetAddress.getByName(parts[0]), port)
    } catch (e: Exception) {
        null
    }

    /** Envía DISCOVER por broadcast y espera una respuesta corta (no bloquea más de ~150 ms). */
    private fun discover(): InetSocketAddress? {
        var ds = discoverySocket
        if (ds == null || ds.isClosed) {
            ds = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(0))
                soTimeout = 150
            }
            bindToWifi(ds)
            discoverySocket = ds
        }
        val body = JSONObject().apply {
            put("deviceId", listener.buildHello().optString("deviceId"))
            put("name", listener.buildHello().optString("name"))
        }
        val msg = Protocol.buildJson(Protocol.DISCOVER, sessionId, body.toString())
        for (addr in broadcastAddresses()) {
            runCatching { ds.send(DatagramPacket(msg, msg.size, InetSocketAddress(addr, Protocol.DISCOVERY_PORT))) }
        }
        val buf = ByteArray(2048)
        val deadline = SystemClock.elapsedRealtime() + 150
        while (SystemClock.elapsedRealtime() < deadline) {
            val p = DatagramPacket(buf, buf.size)
            try {
                ds.receive(p)
            } catch (_: SocketTimeoutException) {
                return null
            } catch (_: Exception) {
                return null
            }
            if (Protocol.readType(buf, p.length) == Protocol.DISCOVER_REPLY) {
                val json = runCatching { JSONObject(Protocol.jsonBody(buf, p.length)) }.getOrNull() ?: continue
                receiverName = json.optString("receiver")
                val port = json.optInt("port", Protocol.MEDIA_PORT)
                return InetSocketAddress(p.address, port)
            }
        }
        return null
    }

    private fun wifiNetwork(): Network? {
        return try {
            connectivity.allNetworks.firstOrNull { n ->
                connectivity.getNetworkCapabilities(n)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
            }
        } catch (e: Exception) {
            null
        }
    }

    /** Usa siempre el WiFi aunque los datos móviles estén activos. */
    private fun bindToWifi(s: DatagramSocket) {
        val n = wifiNetwork() ?: return
        runCatching { n.bindSocket(s) }
    }

    private fun broadcastAddresses(): List<InetAddress> {
        val out = ArrayList<InetAddress>()
        runCatching {
            val n = wifiNetwork()
            val lp = n?.let { connectivity.getLinkProperties(it) }
            lp?.linkAddresses?.forEach { la ->
                val a = la.address
                if (a is Inet4Address) {
                    val ip = a.address
                    val prefix = la.prefixLength
                    val ipInt = ((ip[0].toInt() and 0xFF) shl 24) or ((ip[1].toInt() and 0xFF) shl 16) or
                        ((ip[2].toInt() and 0xFF) shl 8) or (ip[3].toInt() and 0xFF)
                    val mask = if (prefix == 0) 0 else -1 shl (32 - prefix)
                    val bc = ipInt or mask.inv()
                    out.add(InetAddress.getByAddress(byteArrayOf((bc ushr 24).toByte(), (bc ushr 16).toByte(), (bc ushr 8).toByte(), bc.toByte())))
                }
            }
        }
        out.add(InetAddress.getByName("255.255.255.255"))
        return out
    }

    // ------------------------------------------------------------------ recepción

    private fun receiveLoop() {
        val buf = ByteArray(65536)
        val p = DatagramPacket(buf, buf.size)
        while (running) {
            try {
                p.setData(buf, 0, buf.size)
                socket?.receive(p) ?: break
            } catch (_: SocketTimeoutException) {
                continue
            } catch (e: Exception) {
                if (!running) break
                try { Thread.sleep(20) } catch (_: InterruptedException) { break }
                continue
            }
            val len = p.length
            val type = Protocol.readType(buf, len)
            if (type < 0) continue
            val from = p.socketAddress as? InetSocketAddress
            try {
                handle(type, buf, len, from)
            } catch (e: Exception) {
                Log.w(TAG, "paquete inválido", e)
            }
        }
    }

    private fun handle(type: Int, buf: ByteArray, len: Int, from: InetSocketAddress?) {
        val now = SystemClock.elapsedRealtime()
        when (type) {
            Protocol.HELLO_ACK -> {
                val json = JSONObject(Protocol.jsonBody(buf, len))
                if (!json.optBoolean("accepted", true)) {
                    acked = false
                    setState(State.RECHAZADO, json.optString("reason", "El receptor rechazó la conexión"))
                    return
                }
                lastRxMs = now
                receiverName = json.optString("receiver", receiverName)
                json.optJSONObject("decode")?.let { d ->
                    if (d.optBoolean("probed", false)) {
                        listener.onReceiverDecodeCaps(
                            d.optInt("hevcGpuMaxHeight", 0), d.optInt("h264GpuMaxHeight", 0),
                            d.optInt("maxHeight", 0), d.optLong("maxPixelRate", 0), d.optString("gpu", ""),
                        )
                    } else {
                        // El receptor todavía está probando la GPU: sólo el límite (si hay).
                        listener.onReceiverDecodeCaps(-1, -1, d.optInt("maxHeight", 0), d.optLong("maxPixelRate", 0), "")
                    }
                }
                if (target == null && from != null) target = from
                from?.address?.hostAddress?.let { lastKnownHost = it }
                if (!acked || state != State.CONECTADO) {
                    acked = true
                    setState(State.CONECTADO, "Conectado a ${receiverName.ifEmpty { from?.address?.hostAddress ?: "receptor" }}")
                    listener.onConnected(receiverName)
                }
            }
            Protocol.PONG -> {
                lastRxMs = now
                val b = Protocol.le(buf)
                val t = b.getLong(12)
                val rtt = (SystemClock.elapsedRealtimeNanos() / 1000 - t) / 1000.0
                if (rtt in 0.0..5000.0) {
                    rttMs = if (rttMs == 0.0) rtt else rttMs * 0.8 + rtt * 0.2
                    // mínimo que "olvida" despacio, por si la red cambia
                    minRttMs = if (rtt < minRttMs) rtt else minRttMs + 0.02
                }
            }
            Protocol.NACK -> {
                lastRxMs = now
                val t = target ?: return
                for (s in Protocol.readNack(buf, len)) {
                    synchronized(ringLock) {
                        val slot = (s and 0x7FFFFFFF) % RING
                        if (ringSeq[slot] == s) {
                            ring[slot][20] = (ring[slot][20].toInt() or Protocol.FLAG_RETRANSMIT).toByte()
                            sendRaw(ring[slot], ringLen[slot], t)
                            retransmitted++
                        }
                    }
                }
            }
            Protocol.LIMIT -> {
                lastRxMs = now
                val json = runCatching { JSONObject(Protocol.jsonBody(buf, len)) }.getOrNull() ?: return
                listener.onReceiverLimit(json.optInt("maxHeight", 0), json.optLong("maxPixelRate", 0), json.optString("reason"))
            }
            Protocol.KEYFRAME_REQ -> {
                lastRxMs = now
                listener.onKeyframeRequested()
            }
            Protocol.REPORT -> {
                lastRxMs = now
                Protocol.readReport(buf, len)?.let { listener.onReport(it, rttMs, if (minRttMs == Double.MAX_VALUE) rttMs else minRttMs) }
            }
            Protocol.DISCONNECT -> {
                val reason = runCatching { JSONObject(Protocol.jsonBody(buf, len)).optString("reason") }.getOrDefault("")
                listener.onRemoteDisconnect(reason.ifEmpty { "Desconectado desde el receptor" })
            }
        }
    }

    private fun setState(s: State, detail: String) {
        val changed = s != state
        state = s
        if (changed || detail.isNotEmpty()) listener.onStateChanged(s, detail)
    }
}
