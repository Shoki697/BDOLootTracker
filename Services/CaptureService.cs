using System.Buffers.Binary;
using PacketDotNet;
using SharpPcap;

namespace BDOLootTracker.Services;

public sealed class CaptureService : IDisposable
{
    private const ushort BdoServerPort = 8889;

    private readonly Dictionary<ushort, BdoConnection> _connections = new();
    private ICaptureDevice? _device;

    public bool IsRunning { get; private set; }

    public event Action<uint, ulong>? LootReceived;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? CaptureError;

    public void Start(string adapterName)
    {
        if (IsRunning)
            return;

        var device = CaptureDeviceList.Instance
            .FirstOrDefault(d => string.Equals(d.Name, adapterName, StringComparison.OrdinalIgnoreCase));

        if (device == null)
            throw new InvalidOperationException("The selected network adapter could not be found.");

        _connections.Clear();
        _device = device;
        _device.OnPacketArrival += OnPacketArrival;
        _device.Open(DeviceModes.Promiscuous, read_timeout: 1000);
        _device.Filter = $"tcp src port {BdoServerPort}";
        _device.StartCapture();

        IsRunning = true;
        StatusChanged?.Invoke("Connected");
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

            if (tcp == null || tcp.SourcePort != BdoServerPort)
                return;

            var payload = tcp.PayloadData;
            if (payload == null || payload.Length == 0)
                return;

            ushort streamId = tcp.DestinationPort;

            if (!_connections.TryGetValue(streamId, out var connection))
            {
                connection = new BdoConnection();
                connection.Parser.LootReceived += (itemId, quantity) => LootReceived?.Invoke(itemId, quantity);
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

    public void Dispose() => Stop();

    private sealed class BdoConnection
    {
        public TcpStreamReassembler Reassembler { get; } = new();
        public BdoLootParser Parser { get; } = new();
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
        private static readonly byte[] LootSignature = { 0x00, 0x01, 0x00, 0xE0 };
        private const int MinimumLootDataLength = 42;
        private const uint MaxReasonableItemId = 10_000_000;

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
                int signaturePosition = FindLootSignature();

                if (signaturePosition < 0)
                {
                    int keepBytes = LootSignature.Length - 1;
                    if (_buffer.Count > keepBytes)
                        _buffer.RemoveRange(0, _buffer.Count - keepBytes);
                    return;
                }

                if (signaturePosition > 0)
                    _buffer.RemoveRange(0, signaturePosition);

                if (_buffer.Count < MinimumLootDataLength)
                    return;

                ParseLootCandidate();

                // A stabil tesztverzió logikája: csak a 4 byte-os signature-t lépjük át,
                // így közeli loot eseményt sem tudunk kihagyni.
                _buffer.RemoveRange(0, LootSignature.Length);
            }
        }

        private int FindLootSignature()
        {
            if (_buffer.Count < LootSignature.Length)
                return -1;

            for (int i = 0; i <= _buffer.Count - LootSignature.Length; i++)
            {
                if (_buffer[i] == LootSignature[0] &&
                    _buffer[i + 1] == LootSignature[1] &&
                    _buffer[i + 2] == LootSignature[2] &&
                    _buffer[i + 3] == LootSignature[3])
                    return i;
            }

            return -1;
        }

        private void ParseLootCandidate()
        {
            Span<byte> itemBytes = stackalloc byte[4];
            Span<byte> quantityBytes = stackalloc byte[8];

            for (int i = 0; i < 4; i++)
                itemBytes[i] = _buffer[30 + i];

            for (int i = 0; i < 8; i++)
                quantityBytes[i] = _buffer[34 + i];

            uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(itemBytes);
            ulong quantity = BinaryPrimitives.ReadUInt64LittleEndian(quantityBytes);

            if (itemId == 0 || itemId > MaxReasonableItemId)
                return;

            if (quantity == 0 || quantity > 10_000_000_000UL)
                return;

            LootReceived?.Invoke(itemId, quantity);
        }
    }
}
