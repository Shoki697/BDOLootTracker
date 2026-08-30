namespace BDOLootTracker.Models;

public sealed class IgnoredItemRecord
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTime AddedAtUtc { get; init; }

    public string DisplayText => $"{Name}  (ID: {ItemId})";
}
