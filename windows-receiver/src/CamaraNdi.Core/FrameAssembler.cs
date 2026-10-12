namespace CamaraNdi.Core;

/// <summary>Frame codificado completo, listo para el decodificador.</summary>
public sealed class AssembledFrame
{
    public required uint FrameId { get; init; }
    public required bool Keyframe { get; init; }
    public required VideoCodec Codec { get; init; }
    public required long PtsUs { get; init; }
    public required long FirstArrivalMs { get; init; }
    public required long CompletedMs { get; init; }
    public required byte[] Data { get; init; }
    public required int Length { get; init; }
}

/// <summary>
/// Jitter buffer mínimo: reordena paquetes, detecta huecos (para NACK), arma frames
/// y los entrega EN ORDEN. Si un frame no se completa antes de su límite
/// (llegada del primer paquete + JitterMs) se descarta y se exige un keyframe.
/// No es thread-safe: el que lo usa debe sincronizar.
/// </summary>
public sealed class FrameAssembler
{
    private sealed class Slot
    {
        public ushort FragCount;
        public byte[]?[] Frags = Array.Empty<byte[]?>();
        public int[] FragLen = Array.Empty<int>();
        public int Received;
        public int Bytes;
        public bool Keyframe;
        public VideoCodec Codec;
        public long PtsUs;
        public long FirstArrivalMs;
        public long CompletedMs;
        public bool Complete => Received == FragCount;
    }

    private sealed class Missing
    {
        public long DetectedMs;
        public long LastNackMs;
        public int NackCount;
    }

    public const int MaxPendingFrames = 240;
    public const int MissingTrackLimit = 2048;

    private readonly Dictionary<uint, Slot> _frames = new();
    private readonly Dictionary<uint, Missing> _missing = new();
    private bool _started;
    private uint _nextFrameId;
    private bool _seqStarted;
    private uint _highestSeq;

    /// <summary>Tiempo máximo que se espera a un frame incompleto.</summary>
    public int JitterMs { get; set; } = 60;
    /// <summary>Intervalo mínimo entre NACK repetidos del mismo paquete.</summary>
    public int NackRetryMs { get; set; } = 20;

    /// <summary>Con un buffer grande hay tiempo para pedir el mismo paquete más veces.</summary>
    public int MaxNacks => Math.Clamp(JitterMs / 25, 3, 12);
    public int RetryIntervalMs => Math.Max(NackRetryMs, JitterMs / (MaxNacks + 1));

    public bool NeedKeyframe { get; private set; } = true;

    // Contadores acumulados (el que lee calcula deltas).
    public long ExpectedPackets { get; private set; }
    public long ReceivedPackets { get; private set; }
    public long MissingPackets { get; private set; }
    public long RecoveredPackets { get; private set; }
    public long LostPackets { get; private set; }
    public long DuplicatePackets { get; private set; }
    /// <summary>Frames perdidos por la red (o por esperar un keyframe después de una pérdida).</summary>
    public long FramesDropped { get; private set; }
    /// <summary>
    /// Frames saltados porque el PC no alcanzaba a decodificar (<see cref="DropBacklog"/> y la espera
    /// del keyframe que provoca). No es culpa de la red: no se le cuenta al celular como pérdida.
    /// </summary>
    public long FramesSkippedSlowPc { get; private set; }
    public long FramesAssembled { get; private set; }
    public long BytesReceived { get; private set; }
    /// <summary>Frames distintos que empezaron a llegar (mide cuántos frames por segundo manda el celular).</summary>
    public long FramesStarted { get; private set; }
    public int PendingFrames => _frames.Count;

    /// <summary>Se está esperando un keyframe porque se saltó lo acumulado (PC lento), no por la red.</summary>
    private bool _skippingForBacklog;

    private void CountDrop()
    {
        if (_skippingForBacklog) FramesSkippedSlowPc++;
        else FramesDropped++;
    }

    public void Reset()
    {
        _frames.Clear();
        _missing.Clear();
        _started = false;
        _seqStarted = false;
        _skippingForBacklog = false;
        NeedKeyframe = true;
    }

    /// <summary>Marca el flujo como roto (p. ej. error del decodificador): descarta hasta el próximo keyframe.</summary>
    public void ForceKeyframe() => NeedKeyframe = true;

    /// <summary>
    /// Hay demasiados frames esperando (el decodificador no da abasto): se tira todo lo viejo y se
    /// sigue desde el keyframe completo MÁS NUEVO (o se espera el próximo). Así el retraso nunca crece.
    /// Devuelve cuántos frames se descartaron.
    /// </summary>
    public int DropBacklog()
    {
        if (!_started || _frames.Count == 0) return 0;
        uint? newestKey = null;
        uint newest = _nextFrameId;
        foreach (var (id, s) in _frames)
        {
            if ((int)(id - newest) > 0) newest = id;
            if (s.Keyframe && s.Complete && (newestKey == null || (int)(id - newestKey.Value) > 0)) newestKey = id;
        }
        uint target = newestKey ?? newest + 1;
        int dropped = 0;
        foreach (var id in _frames.Keys.ToList())
        {
            if ((int)(id - target) < 0) { _frames.Remove(id); dropped++; }
        }
        FramesSkippedSlowPc += (int)(target - _nextFrameId) > 0 ? (int)(target - _nextFrameId) : dropped;
        _nextFrameId = target;
        NeedKeyframe = true;
        _skippingForBacklog = true;
        return dropped;
    }

    public void OnPacket(in VideoPacketHeader h, ReadOnlySpan<byte> payload, long nowMs)
    {
        ReceivedPackets++;
        BytesReceived += payload.Length + Protocol.VideoHeaderSize;
        TrackSequence(h.Seq, nowMs);

        if (!_started)
        {
            _started = true;
            _nextFrameId = h.FrameId;
        }
        else
        {
            int ahead = (int)(h.FrameId - _nextFrameId);
            if (ahead < 0)
            {
                if (ahead < -1000) { RestartAt(h.FrameId); } // el emisor reinició su contador
                else return; // frame ya entregado o descartado: llega tarde
            }
            else if (ahead > MaxPendingFrames * 4)
            {
                RestartAt(h.FrameId);
            }
        }

        if (!_frames.TryGetValue(h.FrameId, out var slot))
        {
            slot = new Slot
            {
                FragCount = h.FragCount,
                Frags = new byte[]?[h.FragCount],
                FragLen = new int[h.FragCount],
                Keyframe = h.IsKeyframe,
                Codec = h.Codec,
                PtsUs = h.PtsUs,
                FirstArrivalMs = nowMs,
            };
            _frames[h.FrameId] = slot;
            FramesStarted++;
            if (_frames.Count > MaxPendingFrames) TrimPending();
        }
        if (h.FragCount != slot.FragCount || slot.Frags[h.FragIdx] != null)
        {
            DuplicatePackets++;
            return;
        }
        var copy = new byte[payload.Length];
        payload.CopyTo(copy);
        slot.Frags[h.FragIdx] = copy;
        slot.FragLen[h.FragIdx] = payload.Length;
        slot.Received++;
        slot.Bytes += payload.Length;
        if (slot.Complete) slot.CompletedMs = nowMs;
    }

    private void RestartAt(uint frameId)
    {
        _frames.Clear();
        _nextFrameId = frameId;
        NeedKeyframe = true;
        _skippingForBacklog = false;
    }

    private void TrimPending()
    {
        // Nunca dejamos crecer la cola: se descartan los más viejos.
        while (_frames.Count > MaxPendingFrames)
        {
            _frames.Remove(_nextFrameId);
            _nextFrameId++;
            FramesDropped++;
            NeedKeyframe = true;
        }
    }

    private void TrackSequence(uint seq, long nowMs)
    {
        if (!_seqStarted)
        {
            _seqStarted = true;
            _highestSeq = seq;
            ExpectedPackets++;
            return;
        }
        int d = (int)(seq - _highestSeq);
        if (d > 0)
        {
            ExpectedPackets += d;
            if (d > 1)
            {
                if (d - 1 > MissingTrackLimit)
                {
                    // Salto enorme (reconexión): no tiene sentido pedir todo eso.
                    _missing.Clear();
                }
                else
                {
                    for (uint s = _highestSeq + 1; s != seq; s++)
                    {
                        _missing[s] = new Missing { DetectedMs = nowMs };
                        MissingPackets++;
                    }
                }
            }
            _highestSeq = seq;
        }
        else if (d < -MissingTrackLimit * 4)
        {
            // El emisor reinició la numeración.
            _missing.Clear();
            _highestSeq = seq;
        }
        else if (_missing.Remove(seq))
        {
            RecoveredPackets++;
        }
    }

    /// <summary>Números de secuencia a pedir de nuevo ahora mismo.</summary>
    public List<uint> CollectNacks(long nowMs)
    {
        var list = new List<uint>();
        if (_missing.Count == 0) return list;
        List<uint>? expired = null;
        foreach (var (seq, m) in _missing)
        {
            long age = nowMs - m.DetectedMs;
            if (age > Math.Max(JitterMs, 40) || m.NackCount >= MaxNacks && nowMs - m.LastNackMs > RetryIntervalMs)
            {
                (expired ??= new()).Add(seq);
                continue;
            }
            if (m.NackCount < MaxNacks && (m.NackCount == 0 || nowMs - m.LastNackMs >= RetryIntervalMs))
            {
                m.NackCount++;
                m.LastNackMs = nowMs;
                list.Add(seq);
            }
        }
        if (expired != null)
        {
            foreach (var s in expired) _missing.Remove(s);
            LostPackets += expired.Count;
        }
        return list;
    }

    /// <summary>Instante (ms) en el que habrá que tomar una decisión sobre el frame pendiente, o null.</summary>
    public long? NextDeadline()
    {
        if (!_started) return null;
        return DeadlineFor(_nextFrameId);
    }

    private long? DeadlineFor(uint frameId)
    {
        if (_frames.TryGetValue(frameId, out var s)) return s.FirstArrivalMs + JitterMs;
        long? min = null;
        foreach (var (id, slot) in _frames)
        {
            if ((int)(id - frameId) > 0 && (min == null || slot.FirstArrivalMs < min)) min = slot.FirstArrivalMs;
        }
        return min + JitterMs;
    }

    /// <summary>Devuelve el siguiente frame en orden si ya se puede entregar.</summary>
    public AssembledFrame? NextFrame(long nowMs)
    {
        while (_started)
        {
            if (NeedKeyframe) SkipToCompleteKeyframe();

            if (_frames.TryGetValue(_nextFrameId, out var slot))
            {
                if (slot.Complete)
                {
                    _frames.Remove(_nextFrameId);
                    uint id = _nextFrameId++;
                    if (NeedKeyframe && !slot.Keyframe)
                    {
                        CountDrop();
                        continue;
                    }
                    if (slot.Keyframe) { NeedKeyframe = false; _skippingForBacklog = false; }
                    FramesAssembled++;
                    return Build(id, slot);
                }
                if (NeedKeyframe && !slot.Keyframe)
                {
                    // Inútil esperar un frame P si de todas formas necesitamos un keyframe.
                    _frames.Remove(_nextFrameId++);
                    CountDrop();
                    continue;
                }
            }

            var deadline = DeadlineFor(_nextFrameId);
            if (deadline is long d && nowMs >= d)
            {
                // Un frame que no se completó a tiempo: esto sí es la red.
                _frames.Remove(_nextFrameId);
                _nextFrameId++;
                _skippingForBacklog = false;
                FramesDropped++;
                NeedKeyframe = true;
                continue;
            }
            return null;
        }
        return null;
    }

    private void SkipToCompleteKeyframe()
    {
        uint? best = null;
        foreach (var (id, s) in _frames)
        {
            if (s.Keyframe && s.Complete && (int)(id - _nextFrameId) >= 0 && (best == null || (int)(id - best.Value) < 0)) best = id;
        }
        if (best is not uint k || k == _nextFrameId) return;
        while (_nextFrameId != k)
        {
            _frames.Remove(_nextFrameId);
            CountDrop();
            _nextFrameId++;
        }
    }

    private static AssembledFrame Build(uint id, Slot s)
    {
        var data = new byte[s.Bytes];
        int off = 0;
        for (int i = 0; i < s.FragCount; i++)
        {
            Buffer.BlockCopy(s.Frags[i]!, 0, data, off, s.FragLen[i]);
            off += s.FragLen[i];
        }
        return new AssembledFrame
        {
            FrameId = id,
            Keyframe = s.Keyframe,
            Codec = s.Codec,
            PtsUs = s.PtsUs,
            FirstArrivalMs = s.FirstArrivalMs,
            CompletedMs = s.CompletedMs,
            Data = data,
            Length = off,
        };
    }
}
