namespace BDOLootTracker.Services;

/// <summary>
/// TCP stream reassembler that deliberately keeps a small uncommitted tail.
/// Some Npcap/Windows captures contain short overlapping server payloads (for
/// example six zero bytes) immediately followed by a retransmission carrying
/// the real application bytes at the same/overlapping TCP sequence number.
/// If those bytes are emitted immediately, the real retransmission is trimmed
/// and the BDO packet length/signature bytes are permanently lost.
///
/// The retained tail lets a later overlapping segment replace those bytes
/// before they are handed to the BDO parser. This is normal TCP last-arrival
/// overlap handling with a bounded delay and no full-session buffering.
/// </summary>
internal sealed class TcpOverlapSafeReassembler
{
    // Observed capture overlaps are only a few bytes, but keep a comfortable
    // margin without adding meaningful latency on BDO's continuous stream.
    private const int HoldBackBytes = 512;
    private const long SequenceModulus = 1L << 32;

    private bool _initialized;
    private long _nextSequence;
    private long _tailStartSequence;
    private readonly List<byte> _tail = new();
    private readonly SortedDictionary<long, byte[]> _pending = new();

    public long CorrectedOverlapBytes { get; private set; }
    public long CorrectedOverlapSegments { get; private set; }

    public event Action? OverlapCorrected;

    public void Push(uint sequence, byte[] payload, Action<byte[]> onData)
    {
        if (payload.Length == 0)
            return;

        if (!_initialized)
        {
            _initialized = true;
            _nextSequence = sequence;
            _tailStartSequence = sequence;
        }

        long absoluteSequence = UnwrapSequence(sequence);
        Ingest(absoluteSequence, payload);
        DrainPending();
        EmitSafePrefix(onData);
    }

    /// <summary>
    /// Emits the remaining contiguous tail. Call only after packet capture has
    /// stopped, when no later overlapping retransmission can still arrive.
    /// </summary>
    public void Flush(Action<byte[]> onData)
    {
        if (!_initialized)
            return;

        DrainPending();

        if (_tail.Count > 0)
        {
            onData(_tail.ToArray());
            _tailStartSequence += _tail.Count;
            _tail.Clear();
        }
    }

    private long UnwrapSequence(uint sequence)
    {
        long cycleBase = _nextSequence & ~0xFFFF_FFFFL;
        long candidate = cycleBase | sequence;

        if (candidate - _nextSequence > SequenceModulus / 2)
            candidate -= SequenceModulus;
        else if (_nextSequence - candidate > SequenceModulus / 2)
            candidate += SequenceModulus;

        return candidate;
    }

    private void Ingest(long sequence, byte[] payload)
    {
        long end = sequence + payload.Length;

        // Entire retransmission is older than the retained correction window.
        if (end <= _tailStartSequence)
            return;

        // Trim only the portion that has already been committed to the parser.
        if (sequence < _tailStartSequence)
        {
            int trim = checked((int)(_tailStartSequence - sequence));
            payload = payload.AsSpan(trim).ToArray();
            sequence = _tailStartSequence;
            end = sequence + payload.Length;
        }

        // Gap: retain the segment until preceding bytes arrive.
        if (sequence > _nextSequence)
        {
            QueuePending(sequence, payload);
            return;
        }

        bool corrected = false;
        long overlapEnd = Math.Min(end, _nextSequence);
        if (sequence < overlapEnd)
        {
            int tailOffset = checked((int)(sequence - _tailStartSequence));
            int overlapLength = checked((int)(overlapEnd - sequence));

            for (int i = 0; i < overlapLength; i++)
            {
                byte incoming = payload[i];
                int index = tailOffset + i;
                if (_tail[index] != incoming)
                {
                    _tail[index] = incoming;
                    CorrectedOverlapBytes++;
                    corrected = true;
                }
            }
        }

        // Segment extends the contiguous stream beyond what we already have.
        if (end > _nextSequence)
        {
            int extensionOffset = checked((int)(_nextSequence - sequence));
            _tail.AddRange(payload.AsSpan(extensionOffset).ToArray());
            _nextSequence = end;
        }

        if (corrected)
        {
            CorrectedOverlapSegments++;
            OverlapCorrected?.Invoke();
        }
    }

    private void QueuePending(long sequence, byte[] payload)
    {
        if (_pending.TryGetValue(sequence, out byte[]? existing))
        {
            // A larger retransmission normally contains the more complete view
            // of this sequence range. Prefer it until the gap is filled.
            if (payload.Length >= existing.Length)
                _pending[sequence] = payload;
            return;
        }

        _pending[sequence] = payload;
    }

    private void DrainPending()
    {
        while (_pending.Count > 0)
        {
            KeyValuePair<long, byte[]> first = _pending.First();
            if (first.Key > _nextSequence)
                return;

            _pending.Remove(first.Key);
            Ingest(first.Key, first.Value);
        }
    }

    private void EmitSafePrefix(Action<byte[]> onData)
    {
        int emitCount = _tail.Count - HoldBackBytes;
        if (emitCount <= 0)
            return;

        byte[] chunk = _tail.GetRange(0, emitCount).ToArray();
        _tail.RemoveRange(0, emitCount);
        _tailStartSequence += emitCount;
        onData(chunk);
    }
}
