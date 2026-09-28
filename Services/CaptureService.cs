using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BDOLootTracker.Models;
using PacketDotNet;
using SharpPcap;

namespace BDOLootTracker.Services;

public sealed class CaptureService : IDisposable
{
    private static readonly TimeSpan ExitLagRelayFailoverAfter = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ExitLagSuppressedMirrorWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ConnectionIdleCleanupAfter = TimeSpan.FromMinutes(2);

    private readonly object _sync = new();
    private readonly Dictionary<FlowKey, BdoConnection> _connections = new();
    private readonly HashSet<FlowKey> _duplicateRelayFlows = new();
    private readonly List<SuppressedTransferFingerprint> _recentSuppressedTransfers = new();

    private ICaptureDevice? _device;
    private ParserProfile _parserProfile;
    private FlowKey? _activeExitLagFlow;
    private bool _exitLagMode;
    private DateTime _nextConnectionCleanupUtc;

    private long _tcpPayloadBytes;
    private long _serverPayloadBytes;
    private long _validLootCount;
    private long _suppressedTransferCount;
    private long _nonGroundInventoryCandidateCount;
    private long _gatheringPacketLootCount;
    private long _lastServerPacketTicks;
    private long _lastValidLootTicks;

    public CaptureService()
    {
        // Local-only fallback; MainWindow replaces this with the active cached
        // profile before each session starts.
        using var profileService = new ParserProfileService();
        _parserProfile = profileService.LoadActiveProfile();
    }

    public bool IsRunning { get; private set; }
    public bool ExitLagModeEnabled => _exitLagMode;
    public long TcpPayloadBytesObserved => Interlocked.Read(ref _tcpPayloadBytes);
    public long ServerPayloadBytesReceived => Interlocked.Read(ref _serverPayloadBytes);
    public long ValidLootCount => Interlocked.Read(ref _validLootCount);
    public long SuppressedTransferCount => Interlocked.Read(ref _suppressedTransferCount);
    public long NonGroundInventoryCandidateCount => Interlocked.Read(ref _nonGroundInventoryCandidateCount);
    public long GatheringPacketLootCount => Interlocked.Read(ref _gatheringPacketLootCount);
    public DateTime? LastServerPacketUtc => ToUtcDateTime(Interlocked.Read(ref _lastServerPacketTicks));
    public DateTime? LastValidLootUtc => ToUtcDateTime(Interlocked.Read(ref _lastValidLootTicks));
    public string ActiveProfileVersion => _parserProfile.ProfileVersion;
    public int ActiveServerPort => _parserProfile.ServerPort;

    public string ActiveExitLagRelay
    {
        get
        {
            lock (_sync)
                return _activeExitLagFlow?.ToDisplayString() ?? string.Empty;
        }
    }

    public int DuplicateExitLagRelayCount
    {
        get
        {
            lock (_sync)
                return _duplicateRelayFlows.Count;
        }
    }

    public event Action<uint, ulong>? LootReceived;
    // Inventory-add shaped packets that match the active signature/offsets but fail
    // the positive Ground Loot fingerprint. Gathering uses this path because its
    // inventory-add subtype is not always the same as mob ground loot. MainWindow
    // only accepts these candidates when the item itself is strong gathering evidence.
    public event Action<uint, ulong>? NonGroundInventoryCandidateReceived;
    // Confirmed Gathering result packets use their own BDO application packet
    // family and are intentionally independent from the calibrated Ground Loot
    // parser profile.
    public event Action<uint, ulong>? GatheringLootReceived;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? CaptureError;

    public void ConfigureParser(ParserProfile profile)
    {
        if (IsRunning)
            throw new InvalidOperationException("The parser profile cannot be changed while packet capture is running.");

        ParserProfileService.ValidateProfile(profile);
        _parserProfile = profile;
    }

    public void Start(string adapterName, bool exitLagMode = false)
    {
        if (IsRunning)
            return;

        var device = CaptureDeviceList.Instance
            .FirstOrDefault(d => string.Equals(d.Name, adapterName, StringComparison.OrdinalIgnoreCase));

        if (device == null)
            throw new InvalidOperationException("The selected network adapter could not be found.");

        lock (_sync)
        {
            _connections.Clear();
            _duplicateRelayFlows.Clear();
            _recentSuppressedTransfers.Clear();
            _activeExitLagFlow = null;
            _exitLagMode = exitLagMode;
            _nextConnectionCleanupUtc = DateTime.UtcNow.AddSeconds(30);
        }

        Interlocked.Exchange(ref _tcpPayloadBytes, 0);
        Interlocked.Exchange(ref _serverPayloadBytes, 0);
        Interlocked.Exchange(ref _validLootCount, 0);
        Interlocked.Exchange(ref _suppressedTransferCount, 0);
        Interlocked.Exchange(ref _nonGroundInventoryCandidateCount, 0);
        Interlocked.Exchange(ref _gatheringPacketLootCount, 0);
        Interlocked.Exchange(ref _lastServerPacketTicks, 0);
        Interlocked.Exchange(ref _lastValidLootTicks, 0);

        _device = device;
        _device.OnPacketArrival += OnPacketArrival;
        _device.Open(DeviceModes.Promiscuous, read_timeout: 1000);

        // Capture TCP broadly in both modes and do the BDO source-port check in
        // managed code. Manual Calibration already uses the same broad filter.
        // On some Npcap/driver/offload combinations the narrower BPF expression
        // ("tcp src port 8889") can yield no callbacks even though Wireshark and
        // the calibration capture see the traffic. Keeping one capture path also
        // gives diagnostics enough information to distinguish a wrong adapter
        // from a parser/port problem.
        _device.Filter = "tcp";

        _device.StartCapture();

        IsRunning = true;
        StatusChanged?.Invoke(exitLagMode
            ? $"Connected • ExitLag scan • parser {_parserProfile.ProfileVersion}"
            : $"Connected • parser {_parserProfile.ProfileVersion}");
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        try
        {
            _device?.StopCapture();
        }
        finally
        {
            if (_device != null)
            {
                _device.OnPacketArrival -= OnPacketArrival;
                _device.Close();
                _device = null;
            }

            lock (_sync)
            {
                // Keep the last relay/duplicate diagnostics available after STOP.
                // START resets them before the next capture begins.
                _connections.Clear();
            }

            IsRunning = false;
            StatusChanged?.Invoke("Stopped");
        }
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
            var tcp = packet.Extract<TcpPacket>();

            if (tcp == null)
                return;

            byte[]? payload = tcp.PayloadData;
            if (payload == null || payload.Length == 0)
                return;

            Interlocked.Add(ref _tcpPayloadBytes, payload.Length);

            // Standard mode still accepts only server -> client payload from the
            // configured BDO port. The difference is that this filtering happens
            // after capture, rather than in Npcap's BPF layer.
            if (!_exitLagMode && tcp.SourcePort != _parserProfile.ServerPort)
                return;

            var flow = new FlowKey(tcp.SourcePort, tcp.DestinationPort);
            DateTime now = DateTime.UtcNow;
            BdoConnection connection;

            lock (_sync)
            {
                CleanupIdleConnectionsIfNeeded(now);

                if (!_connections.TryGetValue(flow, out BdoConnection? existing))
                {
                    connection = new BdoConnection(_parserProfile, flow);
                    connection.Parser.LootReceived += (itemId, quantity) =>
                        Parser_LootReceived(connection, itemId, quantity);
                    connection.Parser.NonGroundInventoryCandidate += (itemId, quantity) =>
                        Parser_NonGroundInventoryCandidate(connection, itemId, quantity);
                    connection.GatheringParser.LootReceived += (itemId, quantity) =>
                        Parser_GatheringLootReceived(connection, itemId, quantity);
                    connection.Parser.TransferSuppressed += (itemId, quantity) =>
                        Parser_TransferSuppressed(connection, itemId, quantity);
                    _connections[flow] = connection;
                }
                else
                {
                    connection = existing;
                }

                connection.LastPayloadUtc = now;
            }

            // In normal mode all captured payload belongs to the configured BDO
            // server port. In ExitLag mode only count the selected relay after a
            // valid loot packet has identified it, so unrelated TCP traffic does
            // not trigger the parser-health warning.
            if (!_exitLagMode || IsActiveExitLagFlow(flow))
            {
                Interlocked.Add(ref _serverPayloadBytes, payload.Length);
                Interlocked.Exchange(ref _lastServerPacketTicks, now.Ticks);
            }

            connection.Reassembler.Push(
                tcp.SequenceNumber,
                payload,
                data =>
                {
                    connection.Parser.Push(data);
                    connection.GatheringParser.Push(data);
                });
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(ex);
        }
    }

    private void Parser_LootReceived(BdoConnection connection, uint itemId, ulong quantity)
    {
        if (!_exitLagMode)
        {
            EmitLoot(itemId, quantity);
            return;
        }

        bool emit;
        bool relayChanged = false;
        string relayLabel = string.Empty;

        lock (_sync)
        {
            DateTime now = DateTime.UtcNow;
            CleanupRecentSuppressedTransfers(now);

            // ExitLag can deliver mirrored copies of the same BDO stream with
            // slightly different TCP segmentation. If one relay recognized a
            // Storage/Maid/Market transfer marker but another mirror missed that
            // marker, do not let the mirrored inventory-add candidate become the
            // first "valid loot" event and accidentally lock the wrong relay.
            if (WasRecentlySuppressedOnAnotherRelay(connection.Flow, itemId, quantity, now))
            {
                _duplicateRelayFlows.Add(connection.Flow);
                return;
            }

            if (_activeExitLagFlow == null)
            {
                _activeExitLagFlow = connection.Flow;
                relayChanged = true;
                emit = true;
            }
            else if (_activeExitLagFlow.Value.Equals(connection.Flow))
            {
                emit = true;
            }
            else
            {
                bool activeStale = !_connections.TryGetValue(_activeExitLagFlow.Value, out BdoConnection? active) ||
                                   now - active.LastPayloadUtc >= ExitLagRelayFailoverAfter;

                if (activeStale)
                {
                    _duplicateRelayFlows.Add(_activeExitLagFlow.Value);
                    _activeExitLagFlow = connection.Flow;
                    relayChanged = true;
                    emit = true;
                }
                else
                {
                    // ExitLag can mirror the same BDO server stream through more
                    // than one relay. Once one valid relay is locked, loot events
                    // from the other live mirrors are deliberately ignored.
                    _duplicateRelayFlows.Add(connection.Flow);
                    emit = false;
                }
            }

            if (relayChanged)
                relayLabel = _activeExitLagFlow.Value.ToDisplayString();
        }

        if (!emit)
            return;

        if (relayChanged)
        {
            StatusChanged?.Invoke(
                $"Connected • ExitLag relay {relayLabel} • parser {_parserProfile.ProfileVersion}");
        }

        EmitLoot(itemId, quantity);
    }


    private void Parser_GatheringLootReceived(BdoConnection connection, uint itemId, ulong quantity)
    {
        Interlocked.Increment(ref _gatheringPacketLootCount);

        // In standard mode there is only one configured BDO server-port stream.
        // ExitLag may mirror the stream; MainWindow applies a short item/quantity
        // dedupe before accepting these events, so pre-lock candidates are safe.
        if (!_exitLagMode || _activeExitLagFlow == null || IsActiveExitLagFlow(connection.Flow))
            GatheringLootReceived?.Invoke(itemId, quantity);
    }

    private void Parser_NonGroundInventoryCandidate(BdoConnection connection, uint itemId, ulong quantity)
    {
        Interlocked.Increment(ref _nonGroundInventoryCandidateCount);

        // Standard capture has a single server-port stream, so forward directly.
        // With ExitLag, forward candidates from the active relay when one is already
        // known. Before a relay is locked we also forward them; MainWindow performs
        // a short item/quantity dedupe before accepting gathering evidence.
        if (!_exitLagMode || _activeExitLagFlow == null || IsActiveExitLagFlow(connection.Flow))
            NonGroundInventoryCandidateReceived?.Invoke(itemId, quantity);
    }

    private void Parser_TransferSuppressed(BdoConnection connection, uint itemId, ulong quantity)
    {
        if (!_exitLagMode)
        {
            Interlocked.Increment(ref _suppressedTransferCount);
            return;
        }

        lock (_sync)
        {
            DateTime now = DateTime.UtcNow;
            CleanupRecentSuppressedTransfers(now);

            bool alreadySeen = _recentSuppressedTransfers.Any(x =>
                x.ItemId == itemId &&
                x.Quantity == quantity &&
                now - x.SeenUtc <= ExitLagSuppressedMirrorWindow);

            _recentSuppressedTransfers.Add(
                new SuppressedTransferFingerprint(itemId, quantity, connection.Flow, now));

            // Both ExitLag mirrors normally see the same transfer. Count the
            // logical transfer only once, even before an active relay is locked.
            if (!alreadySeen)
                Interlocked.Increment(ref _suppressedTransferCount);
        }
    }

    private bool WasRecentlySuppressedOnAnotherRelay(
        FlowKey flow,
        uint itemId,
        ulong quantity,
        DateTime now)
    {
        return _recentSuppressedTransfers.Any(x =>
            !x.Flow.Equals(flow) &&
            x.ItemId == itemId &&
            x.Quantity == quantity &&
            now - x.SeenUtc <= ExitLagSuppressedMirrorWindow);
    }

    private void CleanupRecentSuppressedTransfers(DateTime now)
    {
        _recentSuppressedTransfers.RemoveAll(x => now - x.SeenUtc > ExitLagSuppressedMirrorWindow);
    }

    private void EmitLoot(uint itemId, ulong quantity)
    {
        Interlocked.Increment(ref _validLootCount);
        Interlocked.Exchange(ref _lastValidLootTicks, DateTime.UtcNow.Ticks);
        LootReceived?.Invoke(itemId, quantity);
    }

    private bool IsActiveExitLagFlow(FlowKey flow)
    {
        lock (_sync)
            return _activeExitLagFlow != null && _activeExitLagFlow.Value.Equals(flow);
    }

    private void CleanupIdleConnectionsIfNeeded(DateTime now)
    {
        if (now < _nextConnectionCleanupUtc)
            return;

        _nextConnectionCleanupUtc = now.AddSeconds(30);
        CleanupRecentSuppressedTransfers(now);
        FlowKey? active = _activeExitLagFlow;

        FlowKey[] stale = _connections
            .Where(pair =>
                (!active.HasValue || !pair.Key.Equals(active.Value)) &&
                now - pair.Value.LastPayloadUtc >= ConnectionIdleCleanupAfter)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (FlowKey key in stale)
        {
            _connections.Remove(key);
            _duplicateRelayFlows.Remove(key);
        }
    }

    public void Dispose() => Stop();

    private static DateTime? ToUtcDateTime(long ticks)
        => ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc);

    private readonly record struct FlowKey(ushort SourcePort, ushort DestinationPort)
    {
        public string ToDisplayString() => $"{SourcePort} → {DestinationPort}";
    }

    private readonly record struct SuppressedTransferFingerprint(
        uint ItemId,
        ulong Quantity,
        FlowKey Flow,
        DateTime SeenUtc);

    private sealed class BdoConnection
    {
        public BdoConnection(ParserProfile profile, FlowKey flow)
        {
            Flow = flow;
            Parser = new BdoLootParser(profile);
            GatheringParser = new BdoGatheringParser();
        }

        public FlowKey Flow { get; }
        public DateTime LastPayloadUtc { get; set; } = DateTime.UtcNow;
        public TcpStreamReassembler Reassembler { get; } = new();
        public BdoLootParser Parser { get; }
        public BdoGatheringParser GatheringParser { get; }
    }

    private sealed class TcpStreamReassembler
    {
        private uint? _nextSequence;
        private readonly SortedDictionary<uint, byte[]> _pending = new();

        public void Push(uint sequence, byte[] payload, Action<byte[]> onData)
        {
            if (payload.Length == 0)
                return;

            _nextSequence ??= sequence;
            uint expected = _nextSequence.Value;

            if (sequence < expected)
            {
                uint overlap = expected - sequence;
                if (overlap >= payload.Length)
                    return;

                int remaining = payload.Length - (int)overlap;
                var trimmed = new byte[remaining];
                Buffer.BlockCopy(payload, (int)overlap, trimmed, 0, remaining);
                payload = trimmed;
                sequence = expected;
            }

            if (sequence > expected)
            {
                if (!_pending.ContainsKey(sequence))
                    _pending.Add(sequence, payload);
                return;
            }

            Append(payload, onData);
            DrainPending(onData);
        }

        private void Append(byte[] payload, Action<byte[]> onData)
        {
            onData(payload);
            _nextSequence += (uint)payload.Length;
        }

        private void DrainPending(Action<byte[]> onData)
        {
            while (_nextSequence != null)
            {
                uint expected = _nextSequence.Value;
                uint? foundKey = null;
                byte[]? foundPayload = null;

                foreach (var pair in _pending)
                {
                    uint sequence = pair.Key;
                    if (sequence > expected)
                        break;

                    byte[] payload = pair.Value;

                    if (sequence < expected)
                    {
                        uint overlap = expected - sequence;
                        if (overlap >= payload.Length)
                        {
                            foundKey = sequence;
                            foundPayload = Array.Empty<byte>();
                            break;
                        }

                        int remaining = payload.Length - (int)overlap;
                        var trimmed = new byte[remaining];
                        Buffer.BlockCopy(payload, (int)overlap, trimmed, 0, remaining);
                        foundKey = sequence;
                        foundPayload = trimmed;
                        break;
                    }

                    foundKey = sequence;
                    foundPayload = payload;
                    break;
                }

                if (foundKey == null)
                    return;

                _pending.Remove(foundKey.Value);

                if (foundPayload is { Length: > 0 })
                    Append(foundPayload, onData);
            }
        }
    }

    /// <summary>
    /// Dedicated Gathering result parser discovered from gather.pcapng
    /// (2026-09-28). Gathering does not use the calibrated mob Ground Loot
    /// packet family. A gather action arrives as a compact result-list packet:
    ///
    ///   length:      3-byte little-endian at +0
    ///   signature:   93 14 01 at +3
    ///   header:      15 bytes
    ///   entry size:  222 bytes
    ///   item id:     entry +0  (uint32 LE)
    ///   quantity:    entry +4  (uint64 LE)
    ///
    /// Observed packet lengths were exactly 237, 459 and 903 bytes
    /// (1, 2 and 4 results). Keeping this parser separate prevents gathering
    /// support from changing or weakening the proven Ground Loot calibration.
    /// </summary>
    private sealed class BdoGatheringParser
    {
        private static readonly byte[] Signature = { 0x93, 0x14, 0x01 };
        private const int SignatureOffset = 3;
        private const int HeaderLength = 15;
        private const int EntryLength = 222;
        private const int MinimumPacketLength = HeaderLength + EntryLength;
        private const int MaximumEntries = 32;
        private const uint MaxReasonableItemId = 10_000_000;
        private const ulong MaxReasonableQuantity = 10_000_000UL;

        private readonly List<byte> _buffer = new();

        public event Action<uint, ulong>? LootReceived;

        public void Push(byte[] data)
        {
            if (data.Length == 0)
                return;

            _buffer.AddRange(data);
            ProcessBuffer();
        }

        private void ProcessBuffer()
        {
            while (true)
            {
                int signaturePosition = FindSignature();
                if (signaturePosition < 0)
                {
                    TrimWhenNoSignature();
                    return;
                }

                int packetStart = signaturePosition - SignatureOffset;
                if (packetStart < 0)
                {
                    RemovePrefix(signaturePosition + 1);
                    continue;
                }

                if (packetStart > 0)
                    RemovePrefix(packetStart);

                if (_buffer.Count < MinimumPacketLength)
                    return;

                int packetLength = ReadPacketLength();
                if (packetLength < MinimumPacketLength ||
                    packetLength > HeaderLength + EntryLength * MaximumEntries ||
                    (packetLength - HeaderLength) % EntryLength != 0)
                {
                    // Signature-like bytes inside an unrelated BDO packet.
                    RemovePrefix(1);
                    continue;
                }

                if (_buffer.Count < packetLength)
                    return;

                int entryCount = (packetLength - HeaderLength) / EntryLength;
                if (entryCount < 1 || entryCount > MaximumEntries)
                {
                    RemovePrefix(packetLength);
                    continue;
                }

                var span = CollectionsMarshal.AsSpan(_buffer);
                var parsed = new List<(uint ItemId, ulong Quantity)>(entryCount);
                bool valid = true;

                for (int i = 0; i < entryCount; i++)
                {
                    int entryOffset = HeaderLength + i * EntryLength;
                    uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(
                        span.Slice(entryOffset, 4));
                    ulong quantity = BinaryPrimitives.ReadUInt64LittleEndian(
                        span.Slice(entryOffset + 4, 8));

                    if (itemId == 0 || itemId > MaxReasonableItemId ||
                        quantity == 0 || quantity > MaxReasonableQuantity)
                    {
                        valid = false;
                        break;
                    }

                    parsed.Add((itemId, quantity));
                }

                if (valid)
                {
                    foreach ((uint itemId, ulong quantity) in parsed)
                        LootReceived?.Invoke(itemId, quantity);
                }

                RemovePrefix(packetLength);
            }
        }

        private int FindSignature()
        {
            if (_buffer.Count < Signature.Length)
                return -1;

            for (int i = 0; i <= _buffer.Count - Signature.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < Signature.Length; j++)
                {
                    if (_buffer[i + j] == Signature[j])
                        continue;

                    match = false;
                    break;
                }

                if (match)
                    return i;
            }

            return -1;
        }

        private int ReadPacketLength()
        {
            if (_buffer.Count < 3)
                return 0;

            return _buffer[0] |
                   (_buffer[1] << 8) |
                   (_buffer[2] << 16);
        }

        private void TrimWhenNoSignature()
        {
            // Keep enough trailing bytes for a split [length][signature] sequence.
            int keep = SignatureOffset + Signature.Length - 1;
            if (_buffer.Count > keep)
                RemovePrefix(_buffer.Count - keep);
        }

        private void RemovePrefix(int count)
        {
            if (count <= 0)
                return;

            if (count >= _buffer.Count)
            {
                _buffer.Clear();
                return;
            }

            _buffer.RemoveRange(0, count);
        }
    }

    private sealed class BdoLootParser
    {
        private readonly ParserProfile _profile;
        private readonly byte[] _signature;
        private readonly List<byte[]> _suppressMarkers;
        private readonly List<(int Offset, byte[] Bytes)> _groundLootChecks;
        private readonly List<byte> _buffer = new();

        private long _bufferBaseOffset;
        private long _lastObservedSuppressMarkerEndOffset = -1;
        private bool _pendingTransferSuppression;
        private DateTime _pendingTransferExpiresUtc;

        public BdoLootParser(ParserProfile profile)
        {
            _profile = profile;
            _signature = ParserProfileService.ParseHex(profile.Signature);
            _groundLootChecks = (profile.GroundLootChecks ?? new List<ParserByteCheck>())
                .Select(x => (x.Offset, ParserProfileService.ParseHex(x.Bytes)))
                .Where(x => x.Item2.Length > 0)
                .ToList();

            // Ground-loot-only profiles are positive allowlists. Transfer markers
            // are kept in the JSON for backwards compatibility with older clients,
            // but this parser deliberately does not arm suppression state in this mode.
            _suppressMarkers = profile.GroundLootOnly
                ? new List<byte[]>()
                : (profile.SuppressIfPrecededBy ?? new List<string>())
                    .Select(ParserProfileService.ParseHex)
                    .Where(x => x.Length > 0)
                    .ToList();
        }

        public event Action<uint, ulong>? LootReceived;
        public event Action<uint, ulong>? NonGroundInventoryCandidate;
        public event Action<uint, ulong>? TransferSuppressed;

        public void Push(byte[] data)
        {
            if (data.Length == 0)
                return;

            _buffer.AddRange(data);
            ProcessBuffer();
        }

        private void ProcessBuffer()
        {
            while (true)
            {
                int signaturePosition = FindSignature();
                if (signaturePosition < 0)
                {
                    // A transfer marker can arrive in its own application packet
                    // well before the inventory-add candidate. Observe it before
                    // the rolling buffer is trimmed so the one-shot transfer state
                    // survives TCP segmentation and intermediate packets.
                    ObserveSuppressMarkersBefore(_buffer.Count);
                    TrimWhenNoSignature();
                    return;
                }

                int candidateStart = signaturePosition - _profile.SignatureOffset;
                if (candidateStart < 0)
                {
                    // Capture started in the middle of an application packet.
                    // Skip this incomplete signature and resynchronize on the next one.
                    RemovePrefix(signaturePosition + 1);
                    continue;
                }

                ObserveSuppressMarkersBefore(candidateStart);
                bool stateSuppress = !_profile.GroundLootOnly && HasPendingTransferSuppression();
                bool suppress = !_profile.GroundLootOnly && (stateSuppress || IsSuppressedByLookback(candidateStart));

                if (candidateStart > 0)
                    RemovePrefix(candidateStart);

                if (_buffer.Count < _profile.MinimumLength)
                    return;

                int packetLength = ReadPacketLength();
                if (packetLength < _profile.MinimumLength || packetLength > _profile.MaximumPacketLength)
                {
                    // False-positive signature. Advance one byte and rescan.
                    RemovePrefix(1);
                    continue;
                }

                if (_buffer.Count < packetLength)
                    return;

                uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(
                    CollectionsMarshal.AsSpan(_buffer).Slice(_profile.ItemIdOffset, 4));
                ulong quantity = BinaryPrimitives.ReadUInt64LittleEndian(
                    CollectionsMarshal.AsSpan(_buffer).Slice(_profile.QuantityOffset, 8));

                bool reasonable =
                    itemId > 0 && itemId <= _profile.MaxReasonableItemId &&
                    quantity > 0 && quantity <= _profile.MaxReasonableQuantity;

                if (_profile.GroundLootOnly && !MatchesGroundLoot(packetLength))
                {
                    // Gathering can use the same inventory-add packet family while
                    // carrying a different subtype/fingerprint from mob ground loot.
                    // Do not count it here. Surface only the parsed candidate and let
                    // MainWindow accept it when the Item ID/name is gathering evidence.
                    // Storage/Market/Maid candidates therefore remain rejected by default.
                    if (reasonable)
                        NonGroundInventoryCandidate?.Invoke(itemId, quantity);

                    RemovePrefix(packetLength);
                    continue;
                }

                if (reasonable)
                {
                    if (suppress)
                    {
                        TransferSuppressed?.Invoke(itemId, quantity);

                        // State suppression is intentionally one-shot. A single
                        // Storage/Maid/Market transfer marker suppresses the next
                        // reasonable inventory-add candidate only, preventing a
                        // stale transfer state from swallowing later real mob loot.
                        if (stateSuppress)
                            ClearPendingTransferSuppression();
                    }
                    else
                    {
                        LootReceived?.Invoke(itemId, quantity);
                    }
                }

                RemovePrefix(packetLength);
            }
        }

        private bool MatchesGroundLoot(int packetLength)
        {
            if (!_profile.GroundLootOnly)
                return true;
            if (_groundLootChecks.Count == 0)
                return false;

            var span = CollectionsMarshal.AsSpan(_buffer);
            foreach ((int offset, byte[] bytes) in _groundLootChecks)
            {
                if (offset < 0 || offset + bytes.Length > packetLength)
                    return false;
                if (!span.Slice(offset, bytes.Length).SequenceEqual(bytes))
                    return false;
            }

            return true;
        }

        private int FindSignature()
        {
            if (_buffer.Count < _signature.Length)
                return -1;

            for (int i = 0; i <= _buffer.Count - _signature.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < _signature.Length; j++)
                {
                    if (_buffer[i + j] == _signature[j])
                        continue;

                    match = false;
                    break;
                }

                if (match)
                    return i;
            }

            return -1;
        }

        private int ReadPacketLength()
        {
            int length = 0;
            for (int i = 0; i < _profile.PacketLengthBytes; i++)
            {
                int index = _profile.PacketLengthOffset + i;
                if (index >= _buffer.Count)
                    return 0;

                length |= _buffer[index] << (8 * i);
            }

            return length;
        }

        private void ObserveSuppressMarkersBefore(int limitExclusive)
        {
            if (_profile.SuppressStateTimeoutMilliseconds <= 0 || _suppressMarkers.Count == 0)
                return;

            int limit = Math.Clamp(limitExclusive, 0, _buffer.Count);
            long newestObservedEnd = _lastObservedSuppressMarkerEndOffset;
            bool sawNewMarker = false;

            foreach (byte[] marker in _suppressMarkers)
            {
                if (marker.Length == 0 || limit < marker.Length)
                    continue;

                int lastStart = limit - marker.Length;
                for (int start = 0; start <= lastStart; start++)
                {
                    if (!MatchesAt(start, marker))
                        continue;

                    long absoluteEnd = _bufferBaseOffset + start + marker.Length;
                    if (absoluteEnd <= _lastObservedSuppressMarkerEndOffset)
                        continue;

                    newestObservedEnd = Math.Max(newestObservedEnd, absoluteEnd);
                    sawNewMarker = true;
                }
            }

            if (!sawNewMarker)
                return;

            _lastObservedSuppressMarkerEndOffset = newestObservedEnd;
            _pendingTransferSuppression = true;
            _pendingTransferExpiresUtc = DateTime.UtcNow.AddMilliseconds(
                _profile.SuppressStateTimeoutMilliseconds);
        }

        private bool HasPendingTransferSuppression()
        {
            if (!_pendingTransferSuppression)
                return false;

            if (DateTime.UtcNow <= _pendingTransferExpiresUtc)
                return true;

            ClearPendingTransferSuppression();
            return false;
        }

        private void ClearPendingTransferSuppression()
        {
            _pendingTransferSuppression = false;
            _pendingTransferExpiresUtc = default;
        }

        private bool IsSuppressedByLookback(int candidateStart)
        {
            if (_suppressMarkers.Count == 0)
                return false;

            int configuredLookback = Math.Max(0, _profile.SuppressLookbackBytes);

            foreach (byte[] marker in _suppressMarkers)
            {
                if (candidateStart < marker.Length)
                    continue;

                if (configuredLookback <= 0)
                {
                    if (MatchesAt(candidateStart - marker.Length, marker))
                        return true;
                    continue;
                }

                int first = Math.Max(0, candidateStart - configuredLookback);
                int last = candidateStart - marker.Length;

                // Legacy/fallback search. The state-based path above is what
                // makes v0.12.3 resilient when intermediate packets or ExitLag
                // relay segmentation move the marker outside the short lookback.
                for (int start = last; start >= first; start--)
                {
                    if (MatchesAt(start, marker))
                        return true;
                }
            }

            return false;
        }

        private bool MatchesAt(int start, byte[] pattern)
        {
            if (start < 0 || start + pattern.Length > _buffer.Count)
                return false;

            for (int i = 0; i < pattern.Length; i++)
            {
                if (_buffer[start + i] != pattern[i])
                    return false;
            }

            return true;
        }

        private void TrimWhenNoSignature()
        {
            int maxMarker = _suppressMarkers.Count == 0 ? 0 : _suppressMarkers.Max(x => x.Length);
            int lookback = Math.Max(0, _profile.SuppressLookbackBytes);
            int keep = Math.Max(
                _profile.SignatureOffset + _signature.Length - 1 + lookback + maxMarker,
                _profile.MinimumLength - 1);

            if (_buffer.Count > keep)
                RemovePrefix(_buffer.Count - keep);
        }

        private void RemovePrefix(int count)
        {
            if (count <= 0)
                return;

            count = Math.Min(count, _buffer.Count);
            _buffer.RemoveRange(0, count);
            _bufferBaseOffset += count;
        }
    }

}
