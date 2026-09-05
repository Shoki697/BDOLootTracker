namespace BDOLootTracker.Models;

public sealed class ParserProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileVersion { get; set; } = string.Empty;
    public string Region { get; set; } = "EU";
    public ushort ServerPort { get; set; } = 8889;

    // Hex byte pattern that identifies a loot/inventory-add packet.
    // Example: "1E 16 01". SignatureOffset is relative to the beginning
    // of the application packet, not to the TCP payload.
    public string Signature { get; set; } = string.Empty;
    public int SignatureOffset { get; set; } = 3;

    // BDO application packets currently begin with a little-endian 3-byte length.
    // Keeping this in the profile allows framing changes to be shipped as data.
    public int PacketLengthOffset { get; set; } = 0;
    public int PacketLengthBytes { get; set; } = 3;
    public int MaximumPacketLength { get; set; } = 2_000_000;

    public int ItemIdOffset { get; set; } = 28;
    public int QuantityOffset { get; set; } = 32;
    public int MinimumLength { get; set; } = 40;

    public uint MaxReasonableItemId { get; set; } = 10_000_000;
    public ulong MaxReasonableQuantity { get; set; } = 10_000_000_000UL;

    // Optional byte sequences associated with non-loot inventory transfers.
    // SuppressLookbackBytes keeps the legacy byte-window check for backwards
    // compatibility. SuppressStateTimeoutMilliseconds also lets a detected
    // transfer marker arm a one-shot suppression state for the next reasonable
    // loot candidate, which is more robust when TCP/relay framing inserts
    // intermediate application packets between the marker and inventory-add.
    public int SuppressLookbackBytes { get; set; }
    public int SuppressStateTimeoutMilliseconds { get; set; }
    public List<string> SuppressIfPrecededBy { get; set; } = new();
}
