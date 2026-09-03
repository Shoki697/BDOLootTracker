using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BDOLootTracker.Models;
using PacketDotNet;
using SharpPcap;

namespace BDOLootTracker.Services;

public sealed class CaptureService : IDisposable
{
    private readonly Dictionary<ushort, BdoConnection> _connections = new();
    private ICaptureDevice? _device;
    private ParserProfile _parserProfile;
    private long _serverPayloadBytes;
    private long _validLootCount;
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
    public long ServerPayloadBytesReceived => Interlocked.Read(ref _serverPayloadBytes);
    public long ValidLootCount => Interlocked.Read(ref _validLootCount);
    public DateTime? LastServerPacketUtc => ToUtcDateTime(Interlocked.Read(ref _lastServerPacketTicks));
    public DateTime? LastValidLootUtc => ToUtcDateTime(Interlocked.Read(ref _lastValidLootTicks));
    public string ActiveProfileVersion => _parserProfile.ProfileVersion;

    public event Action<uint, ulong>? LootReceived;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? CaptureError;

    public void ConfigureParser(ParserProfile profile)
    {
        if (IsRunning)
            throw new InvalidOperationException("The parser profile cannot be changed while packet capture is running.");

        ParserProfileService.ValidateProfile(profile);
        _parserProfile = profile;
    }

    public void Start(string adapterName)
    {
        if (IsRunning)
            return;

        var device = CaptureDeviceList.Instance
            .FirstOrDefault(d => string.Equals(d.Name, adapterName, StringComparison.OrdinalIgnoreCase));

        if (device == null)
            throw new InvalidOperationException("The selected network adapter could not be found.");

        _connections.Clear();
        Interlocked.Exchange(ref _serverPayloadBytes, 0);
        Interlocked.Exchange(ref _validLootCount, 0);
        Interlocked.Exchange(ref _lastServerPacketTicks, 0);
        Interlocked.Exchange(ref _lastValidLootTicks, 0);

        _device = device;
        _device.OnPacketArrival += OnPacketArrival;
        _device.Open(DeviceModes.Promiscuous, read_timeout: 1000);
        _device.Filter = $"tcp src port {_parserProfile.ServerPort}";
        _device.StartCapture();

        IsRunning = true;
        StatusChanged?.Invoke($"Connected • parser {_parserProfile.ProfileVersion}");
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

            _connections.Clear();
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

            if (tcp == null || tcp.SourcePort != _parserProfile.ServerPort)
                return;

            var payload = tcp.PayloadData;
            if (payload == null || payload.Length == 0)
                return;

            Interlocked.Add(ref _serverPayloadBytes, payload.Length);
            Interlocked.Exchange(ref _lastServerPacketTicks, DateTime.UtcNow.Ticks);

            ushort streamId = tcp.DestinationPort;

            if (!_connections.TryGetValue(streamId, out var connection))
            {
                connection = new BdoConnection(_parserProfile);
                connection.Parser.LootReceived += Parser_LootReceived;
                _connections[streamId] = connection;
            }

            connection.Reassembler.Push(
                tcp.SequenceNumber,
                payload,
                data => connection.Parser.Push(data));
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(ex);
        }
    }

    private void Parser_LootReceived(uint itemId, ulong quantity)
    {
        Interlocked.Increment(ref _validLootCount);
        Interlocked.Exchange(ref _lastValidLootTicks, DateTime.UtcNow.Ticks);
        LootReceived?.Invoke(itemId, quantity);
    }

    public void Dispose() => Stop();

    private static DateTime? ToUtcDateTime(long ticks)
        => ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc);

    private sealed class BdoConnection
    {
        public BdoConnection(ParserProfile profile)
        {
            Parser = new BdoLootParser(profile);
        }

        public TcpStreamReassembler Reassembler { get; } = new();
        public BdoLootParser Parser { get; }
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

    private sealed class BdoLootParser
    {
        private readonly ParserProfile _profile;
        private readonly byte[] _signature;
        private readonly List<byte[]> _suppressPrefixes;
        private readonly List<byte> _buffer = new();

        public BdoLootParser(ParserProfile profile)
        {
            _profile = profile;
            _signature = ParserProfileService.ParseHex(profile.Signature);
            _suppressPrefixes = (profile.SuppressIfPrecededBy ?? new List<string>())
                .Select(ParserProfileService.ParseHex)
                .Where(x => x.Length > 0)
                .ToList();
        }

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

                int candidateStart = signaturePosition - _profile.SignatureOffset;
                if (candidateStart < 0)
                {
                    // Capture started in the middle of an application packet.
                    // Skip this incomplete signature and resynchronize on the next one.
                    _buffer.RemoveRange(0, signaturePosition + 1);
                    continue;
                }

                bool suppress = IsSuppressed(candidateStart);

                if (candidateStart > 0)
                    _buffer.RemoveRange(0, candidateStart);

                if (_buffer.Count < _profile.MinimumLength)
                    return;

                int packetLength = ReadPacketLength();
                if (packetLength < _profile.MinimumLength || packetLength > _profile.MaximumPacketLength)
                {
                    // False-positive signature. Advance one byte and rescan.
                    _buffer.RemoveAt(0);
                    continue;
                }

                if (_buffer.Count < packetLength)
                    return;

                uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(
                    CollectionsMarshal.AsSpan(_buffer).Slice(_profile.ItemIdOffset, 4));
                ulong quantity = BinaryPrimitives.ReadUInt64LittleEndian(
                    CollectionsMarshal.AsSpan(_buffer).Slice(_profile.QuantityOffset, 8));

                if (!suppress &&
                    itemId > 0 && itemId <= _profile.MaxReasonableItemId &&
                    quantity > 0 && quantity <= _profile.MaxReasonableQuantity)
                {
                    LootReceived?.Invoke(itemId, quantity);
                }

                _buffer.RemoveRange(0, packetLength);
            }
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

        private bool IsSuppressed(int candidateStart)
        {
            foreach (byte[] prefix in _suppressPrefixes)
            {
                if (candidateStart < prefix.Length)
                    continue;

                int start = candidateStart - prefix.Length;
                bool match = true;
                for (int i = 0; i < prefix.Length; i++)
                {
                    if (_buffer[start + i] == prefix[i])
                        continue;

                    match = false;
                    break;
                }

                if (match)
                    return true;
            }

            return false;
        }

        private void TrimWhenNoSignature()
        {
            int maxPrefix = _suppressPrefixes.Count == 0 ? 0 : _suppressPrefixes.Max(x => x.Length);
            int keep = Math.Max(
                _profile.SignatureOffset + _signature.Length - 1 + maxPrefix,
                _profile.MinimumLength - 1);

            if (_buffer.Count > keep)
                _buffer.RemoveRange(0, _buffer.Count - keep);
        }
    }
}
