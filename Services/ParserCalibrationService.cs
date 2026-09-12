using System.Buffers.Binary;
using BDOLootTracker.Models;
using PacketDotNet;
using SharpPcap;

namespace BDOLootTracker.Services;

/// <summary>
/// Short-lived guided packet capture used by Settings -> Network -> Manual Calibration.
/// It never sends traffic and never stores a full pcap. Only the reassembled TCP payload
/// required to infer the loot layout / transfer marker is kept in memory.
/// </summary>
public sealed class ParserCalibrationService : IDisposable
{
    private const int MaxFlows = 200;
    private const int MaxBytesPerFlow = 4 * 1024 * 1024;
    private const long MaxTotalCapturedBytes = 64L * 1024 * 1024;
    private const int MaxPacketLengthForDiscovery = 64 * 1024;
    private const ulong MaxDiscoveryQuantity = 10_000_000UL;
    private const uint BlackStoneItemId = 16001;

    private readonly object _sync = new();
    private readonly Dictionary<FlowKey, FlowCapture> _flows = new();
    private ICaptureDevice? _device;
    private long _totalPayloadBytes;

    public bool IsCapturing { get; private set; }

    public void Start(string adapterName)
    {
        if (IsCapturing)
            throw new InvalidOperationException("Calibration capture is already running.");

        var device = CaptureDeviceList.Instance
            .FirstOrDefault(d => string.Equals(d.Name, adapterName, StringComparison.OrdinalIgnoreCase));

        if (device == null)
            throw new InvalidOperationException("The selected network adapter could not be found.");

        lock (_sync)
            _flows.Clear();
        Interlocked.Exchange(ref _totalPayloadBytes, 0);

        _device = device;
        _device.OnPacketArrival += OnPacketArrival;
        _device.Open(DeviceModes.Promiscuous, read_timeout: 1000);

        // Calibration intentionally observes TCP broadly. This allows the mob-loot
        // step to rediscover a changed BDO source port and also works with ExitLag.
        _device.Filter = "tcp";
        _device.StartCapture();
        IsCapturing = true;
    }

    public CalibrationCapture Stop()
    {
        if (!IsCapturing)
            return new CalibrationCapture(Array.Empty<CalibrationFlowSnapshot>());

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

            IsCapturing = false;
        }

        lock (_sync)
        {
            var snapshots = _flows
                .Where(x => x.Value.Payload.Count > 0)
                .Select(x => new CalibrationFlowSnapshot(
                    x.Key.SourcePort,
                    x.Key.DestinationPort,
                    x.Value.Payload.ToArray()))
                .ToArray();

            return new CalibrationCapture(snapshots);
        }
    }

    public MobCalibrationResult AnalyzeMobLoot(
        CalibrationCapture capture,
        ParserProfile seed,
        IReadOnlySet<uint> knownItemIds,
        bool exitLagMode)
    {
        if (capture.Flows.Count == 0)
            return MobCalibrationResult.Failed("No TCP payload was captured. Check the selected network adapter and try again.");

        var known = new HashSet<uint>(knownItemIds.Where(x => x > 0));
        // Silver and Black Stone are useful anchors even if the local Garmoth DB is stale.
        known.Add(1);
        known.Add(BlackStoneItemId);

        byte[] seedSignature = ParserProfileService.ParseHex(seed.Signature);
        int signatureLength = Math.Clamp(seedSignature.Length, 2, 8);

        Dictionary<CandidateKey, CandidateStat> candidates = DiscoverLootCandidates(
            capture,
            seed,
            known,
            signatureLength,
            quantityDeltaMin: 4,
            quantityDeltaMax: 4);

        // A future patch could insert a few bytes between item id and quantity.
        // Only broaden the search if the normal contiguous layout did not produce
        // a convincing candidate; this keeps false positives low in the common case.
        if (candidates.Count == 0 || candidates.Values.Max(x => x.PacketStarts.Count) < 5)
        {
            candidates = DiscoverLootCandidates(
                capture,
                seed,
                known,
                signatureLength,
                quantityDeltaMin: 4,
                quantityDeltaMax: 12);
        }

        var ranked = candidates
            .Select(x => new
            {
                Key = x.Key,
                Count = x.Value.PacketStarts.Count,
                ItemCount = x.Value.ItemIds.Count,
                MinPacketLength = x.Value.MinimumPacketLength
            })
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.ItemCount)
            .ToArray();

        if (ranked.Length == 0)
        {
            return MobCalibrationResult.Failed(
                "No repeatable known-loot packet layout was found. Collect more normal mob loot and make sure the item database has been updated.");
        }

        var best = ranked[0];
        int runnerUp = ranked.Length > 1 ? ranked[1].Count : 0;
        double confidence = runnerUp <= 0
            ? 0.99
            : 0.5 + 0.5 * Math.Max(0.0, 1.0 - runnerUp / (double)best.Count);

        if (best.Count < 5 || (runnerUp > 0 && best.Count < Math.Ceiling(runnerUp * 1.35)))
        {
            return MobCalibrationResult.Failed(
                $"The capture contained possible loot layouts, but confidence was too low ({best.Count} matching packets). Collect 5–10 more normal loot events and retry.");
        }

        ParserProfile profile = Clone(seed);
        profile.ServerPort = exitLagMode ? seed.ServerPort : best.Key.SourcePort;
        profile.Signature = best.Key.Signature;
        profile.ItemIdOffset = best.Key.ItemOffset;
        profile.QuantityOffset = best.Key.QuantityOffset;
        int offsetShift = Math.Max(0, profile.ItemIdOffset - seed.ItemIdOffset);
        profile.MinimumLength = Math.Max(
            seed.MinimumLength + offsetShift,
            Math.Max(profile.SignatureOffset + signatureLength,
                Math.Max(profile.ItemIdOffset + 4, profile.QuantityOffset + 8)));

        return new MobCalibrationResult(
            true,
            profile,
            best.Count,
            Math.Clamp(confidence, 0.0, 1.0),
            $"Detected {best.Key.Signature} • item +{best.Key.ItemOffset} • quantity +{best.Key.QuantityOffset} • {best.Count} matching loot packets.");
    }

    public TransferCalibrationResult AnalyzeTransfer(
        CalibrationCapture capture,
        ParserProfile profile,
        uint itemId,
        ulong quantity,
        string label)
    {
        byte[] signature = ParserProfileService.ParseHex(profile.Signature);
        var markerScores = new Dictionary<string, MarkerScore>(StringComparer.OrdinalIgnoreCase);
        int targetPackets = 0;

        foreach (CalibrationFlowSnapshot flow in capture.Flows)
        {
            byte[] data = flow.Payload;
            foreach (int packetStart in FindMatchingPacketStarts(data, profile, signature, itemId, quantity))
            {
                targetPackets++;
                int lookback = Math.Max(256, profile.SuppressLookbackBytes);
                int from = Math.Max(0, packetStart - lookback);
                int to = Math.Max(from, packetStart - 5);

                for (int i = from; i <= to; i++)
                {
                    // Most BDO Storage / Maid / Market control frames seen so far
                    // use one of these two small marker shapes. The guided context
                    // tells us this is a non-loot transfer, so we can safely look for
                    // the closest repeated control marker before the inventory-add.
                    if (i + 6 <= packetStart &&
                        data[i] == 0x06 && data[i + 1] == 0x00 && data[i + 2] == 0x00)
                    {
                        AddMarkerCandidate(markerScores, data.AsSpan(i, 6), packetStart - (i + 6), profile);
                    }

                    if (i + 5 <= packetStart &&
                        data[i] == 0x28 && data[i + 1] == 0x00 && data[i + 2] == 0x00)
                    {
                        AddMarkerCandidate(markerScores, data.AsSpan(i, 5), packetStart - (i + 5), profile);
                    }
                }
            }
        }

        if (targetPackets == 0)
        {
            return TransferCalibrationResult.Failed(
                $"The {label} capture did not contain the expected Black Stone x{quantity:N0} inventory-add packet. Start capture first, perform exactly one withdrawal, wait a moment, then stop the capture.");
        }

        var best = markerScores
            .OrderByDescending(x => x.Value.Occurrences)
            .ThenByDescending(x => x.Value.ShapeSimilarity)
            .ThenBy(x => x.Value.TotalDistance)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(best.Key) || best.Value == null)
        {
            return TransferCalibrationResult.Failed(
                $"The {label} inventory-add was found, but no reliable transfer marker was detected in front of it.");
        }

        // Require the candidate to at least resemble the small control-frame shapes
        // used by known profiles. This avoids learning random game payload bytes.
        if (best.Value.ShapeSimilarity < 0.45)
        {
            return TransferCalibrationResult.Failed(
                $"The {label} inventory-add was found, but the preceding marker confidence was too low. Retry the step with no other inventory actions during the capture.");
        }

        ParserProfile updated = Clone(profile);
        var markers = new List<string>(updated.SuppressIfPrecededBy ?? new List<string>());
        if (!markers.Any(x => string.Equals(NormalizeHex(x), best.Key, StringComparison.OrdinalIgnoreCase)))
            markers.Add(best.Key);

        // Keep the list bounded so repeated weekly calibrations cannot grow it forever.
        // Preserve the newest learned entries while retaining the classic BE16 fallback.
        updated.SuppressIfPrecededBy = markers
            .Select(NormalizeHex)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(12)
            .ToList();
        updated.SuppressLookbackBytes = Math.Max(updated.SuppressLookbackBytes, 96);
        updated.SuppressStateTimeoutMilliseconds = Math.Max(updated.SuppressStateTimeoutMilliseconds, 2000);

        double confidence = Math.Clamp(
            0.55 + Math.Min(0.25, best.Value.Occurrences * 0.10) + best.Value.ShapeSimilarity * 0.20,
            0.0,
            0.99);

        return new TransferCalibrationResult(
            true,
            updated,
            best.Key,
            targetPackets,
            confidence,
            $"{label} marker detected: {best.Key}.");
    }

    public static uint CalibrationItemId => BlackStoneItemId;
    public static ulong CalibrationQuantity => 100;

    private Dictionary<CandidateKey, CandidateStat> DiscoverLootCandidates(
        CalibrationCapture capture,
        ParserProfile seed,
        HashSet<uint> known,
        int signatureLength,
        int quantityDeltaMin,
        int quantityDeltaMax)
    {
        var candidates = new Dictionary<CandidateKey, CandidateStat>();
        int flowIndex = 0;

        foreach (CalibrationFlowSnapshot flow in capture.Flows)
        {
            byte[] data = flow.Payload;
            int minimumFrame = Math.Max(24, seed.SignatureOffset + signatureLength);

            for (int start = 0; start <= data.Length - minimumFrame; start++)
            {
                if (!TryReadPacketLength(data, start, seed.PacketLengthOffset, seed.PacketLengthBytes, out int length))
                    continue;

                if (length < minimumFrame || length > Math.Min(seed.MaximumPacketLength, MaxPacketLengthForDiscovery))
                    continue;

                int end = start + length;
                if (end > data.Length)
                    continue;

                if (!NextBoundaryLooksPlausible(data, end, seed))
                    continue;

                int maxItemOffset = Math.Min(96, length - 12);
                for (int itemOffset = 8; itemOffset <= maxItemOffset; itemOffset++)
                {
                    uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + itemOffset, 4));
                    if (!known.Contains(itemId))
                        continue;

                    for (int delta = quantityDeltaMin; delta <= quantityDeltaMax; delta++)
                    {
                        int quantityOffset = itemOffset + delta;
                        if (quantityOffset + 8 > length)
                            break;

                        ulong quantity = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(start + quantityOffset, 8));
                        if (quantity == 0 || quantity > Math.Min(seed.MaxReasonableQuantity, MaxDiscoveryQuantity))
                            continue;

                        if (seed.SignatureOffset + signatureLength > length)
                            continue;

                        string signature = ToHex(data.AsSpan(start + seed.SignatureOffset, signatureLength));
                        var key = new CandidateKey(flow.SourcePort, signature, itemOffset, quantityOffset);
                        if (!candidates.TryGetValue(key, out CandidateStat? stat))
                        {
                            stat = new CandidateStat();
                            candidates[key] = stat;
                        }

                        long packetToken = ((long)flowIndex << 32) | (uint)start;
                        stat.PacketStarts.Add(packetToken);
                        stat.ItemIds.Add(itemId);
                        if (stat.MinimumPacketLength == 0 || length < stat.MinimumPacketLength)
                            stat.MinimumPacketLength = length;
                    }
                }
            }

            flowIndex++;
        }

        return candidates;
    }

    private static IEnumerable<int> FindMatchingPacketStarts(
        byte[] data,
        ParserProfile profile,
        byte[] signature,
        uint itemId,
        ulong quantity)
    {
        int required = Math.Max(
            profile.MinimumLength,
            Math.Max(profile.ItemIdOffset + 4, profile.QuantityOffset + 8));

        for (int start = 0; start <= data.Length - required; start++)
        {
            if (!TryReadPacketLength(data, start, profile.PacketLengthOffset, profile.PacketLengthBytes, out int length))
                continue;

            if (length < required || length > profile.MaximumPacketLength || start + length > data.Length)
                continue;

            if (profile.SignatureOffset + signature.Length > length)
                continue;

            if (!data.AsSpan(start + profile.SignatureOffset, signature.Length).SequenceEqual(signature))
                continue;

            uint foundId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + profile.ItemIdOffset, 4));
            ulong foundQty = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(start + profile.QuantityOffset, 8));
            if (foundId == itemId && foundQty == quantity)
                yield return start;
        }
    }

    private static void AddMarkerCandidate(
        Dictionary<string, MarkerScore> scores,
        ReadOnlySpan<byte> marker,
        int distance,
        ParserProfile profile)
    {
        string hex = ToHex(marker);
        if (!scores.TryGetValue(hex, out MarkerScore? score))
        {
            score = new MarkerScore();
            scores[hex] = score;
        }

        score.Occurrences++;
        score.TotalDistance += Math.Max(0, distance);
        score.ShapeSimilarity = Math.Max(score.ShapeSimilarity, CalculateShapeSimilarity(marker, profile));
    }

    private static double CalculateShapeSimilarity(ReadOnlySpan<byte> marker, ParserProfile profile)
    {
        double best = 0.0;
        foreach (string existingText in profile.SuppressIfPrecededBy ?? new List<string>())
        {
            byte[] existing;
            try
            {
                existing = ParserProfileService.ParseHex(existingText);
            }
            catch
            {
                continue;
            }

            if (existing.Length != marker.Length)
                continue;

            int same = 0;
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] == marker[i])
                    same++;
            }

            best = Math.Max(best, same / (double)existing.Length);
        }

        // Known control-frame prefixes are a useful fallback when all old marker
        // bytes changed in a maintenance patch.
        if (marker.Length >= 5 && marker[0] == 0x06 && marker[1] == 0x00 && marker[2] == 0x00)
            best = Math.Max(best, 0.55);
        if (marker.Length >= 5 && marker[0] == 0x28 && marker[1] == 0x00 && marker[2] == 0x00)
            best = Math.Max(best, 0.50);

        return best;
    }

    private static bool NextBoundaryLooksPlausible(byte[] data, int end, ParserProfile seed)
    {
        // End-of-capture or a partial final packet is acceptable.
        if (end >= data.Length - seed.PacketLengthBytes)
            return true;

        if (!TryReadPacketLength(data, end, seed.PacketLengthOffset, seed.PacketLengthBytes, out int nextLength))
            return false;

        return nextLength >= 3 && nextLength <= seed.MaximumPacketLength;
    }

    private static bool TryReadPacketLength(
        byte[] data,
        int packetStart,
        int lengthOffset,
        int lengthBytes,
        out int length)
    {
        length = 0;
        int index = packetStart + lengthOffset;
        if (index < 0 || lengthBytes < 1 || lengthBytes > 4 || index + lengthBytes > data.Length)
            return false;

        for (int i = 0; i < lengthBytes; i++)
            length |= data[index + i] << (8 * i);

        return length > 0;
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var tcp = packet.Extract<TcpPacket>();
            if (tcp == null)
                return;

            byte[]? payload = tcp.PayloadData;
            if (payload == null || payload.Length == 0)
                return;

            if (Interlocked.Read(ref _totalPayloadBytes) >= MaxTotalCapturedBytes)
                return;

            var key = new FlowKey(tcp.SourcePort, tcp.DestinationPort);
            FlowCapture flow;

            lock (_sync)
            {
                if (!_flows.TryGetValue(key, out FlowCapture? existing))
                {
                    if (_flows.Count >= MaxFlows)
                        return;

                    flow = new FlowCapture();
                    _flows[key] = flow;
                }
                else
                {
                    flow = existing;
                }
            }

            lock (flow.Sync)
            {
                if (flow.Payload.Count >= MaxBytesPerFlow)
                    return;

                flow.Reassembler.Push(tcp.SequenceNumber, payload, bytes =>
                {
                    int remaining = MaxBytesPerFlow - flow.Payload.Count;
                    if (remaining <= 0)
                        return;

                    int count = Math.Min(remaining, bytes.Length);
                    long totalRemaining = MaxTotalCapturedBytes - Interlocked.Read(ref _totalPayloadBytes);
                    if (totalRemaining <= 0)
                        return;

                    count = (int)Math.Min(count, totalRemaining);
                    if (count > 0)
                    {
                        flow.Payload.AddRange(bytes.AsSpan(0, count).ToArray());
                        Interlocked.Add(ref _totalPayloadBytes, count);
                    }
                });
            }
        }
        catch
        {
            // Calibration is best-effort. A malformed/unrelated packet must not
            // abort the guided capture.
        }
    }

    public void Dispose()
    {
        if (IsCapturing)
            _ = Stop();
    }

    public static ParserProfile Clone(ParserProfile source)
        => new()
        {
            SchemaVersion = source.SchemaVersion,
            ProfileVersion = source.ProfileVersion,
            Region = source.Region,
            ServerPort = source.ServerPort,
            Signature = source.Signature,
            SignatureOffset = source.SignatureOffset,
            PacketLengthOffset = source.PacketLengthOffset,
            PacketLengthBytes = source.PacketLengthBytes,
            MaximumPacketLength = source.MaximumPacketLength,
            ItemIdOffset = source.ItemIdOffset,
            QuantityOffset = source.QuantityOffset,
            MinimumLength = source.MinimumLength,
            MaxReasonableItemId = source.MaxReasonableItemId,
            MaxReasonableQuantity = source.MaxReasonableQuantity,
            SuppressLookbackBytes = source.SuppressLookbackBytes,
            SuppressStateTimeoutMilliseconds = source.SuppressStateTimeoutMilliseconds,
            SuppressIfPrecededBy = new List<string>(source.SuppressIfPrecededBy ?? new List<string>())
        };

    private static string NormalizeHex(string value)
    {
        try
        {
            return ToHex(ParserProfileService.ParseHex(value));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
        => string.Join(" ", bytes.ToArray().Select(x => x.ToString("X2")));

    private readonly record struct FlowKey(ushort SourcePort, ushort DestinationPort);
    private readonly record struct CandidateKey(ushort SourcePort, string Signature, int ItemOffset, int QuantityOffset);

    private sealed class CandidateStat
    {
        public HashSet<long> PacketStarts { get; } = new();
        public HashSet<uint> ItemIds { get; } = new();
        public int MinimumPacketLength { get; set; }
    }

    private sealed class MarkerScore
    {
        public int Occurrences { get; set; }
        public int TotalDistance { get; set; }
        public double ShapeSimilarity { get; set; }
    }

    private sealed class FlowCapture
    {
        public object Sync { get; } = new();
        public List<byte> Payload { get; } = new();
        public TcpStreamReassembler Reassembler { get; } = new();
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
                    if (pair.Key > expected)
                        break;

                    if (pair.Key < expected)
                    {
                        uint overlap = expected - pair.Key;
                        foundKey = pair.Key;
                        if (overlap >= pair.Value.Length)
                        {
                            foundPayload = Array.Empty<byte>();
                        }
                        else
                        {
                            int remaining = pair.Value.Length - (int)overlap;
                            var trimmed = new byte[remaining];
                            Buffer.BlockCopy(pair.Value, (int)overlap, trimmed, 0, remaining);
                            foundPayload = trimmed;
                        }

                        break;
                    }

                    foundKey = pair.Key;
                    foundPayload = pair.Value;
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
}

public sealed record CalibrationCapture(IReadOnlyList<CalibrationFlowSnapshot> Flows);
public sealed record CalibrationFlowSnapshot(ushort SourcePort, ushort DestinationPort, byte[] Payload);

public sealed record MobCalibrationResult(
    bool Success,
    ParserProfile? Profile,
    int SampleCount,
    double Confidence,
    string Message)
{
    public static MobCalibrationResult Failed(string message)
        => new(false, null, 0, 0, message);
}

public sealed record TransferCalibrationResult(
    bool Success,
    ParserProfile? Profile,
    string Marker,
    int SampleCount,
    double Confidence,
    string Message)
{
    public static TransferCalibrationResult Failed(string message)
        => new(false, null, string.Empty, 0, 0, message);
}
