namespace BDOLootTracker.Models;

public sealed class SessionLootSnapshot
{
    public uint ItemId { get; init; }
    public ulong Quantity { get; init; }
    public string Name { get; init; } = string.Empty;
    public long UnitPrice { get; init; }
    public bool IsTrash { get; init; }
    public string? IconPath { get; init; }
}
