namespace BDOLootTracker.Models;

public sealed class ItemDefinition
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? IconUrl { get; init; }
    public string? LocalIconPath { get; init; }
    public long UnitPrice { get; init; }
    public bool IsTrash { get; init; }
    public int Grade { get; init; }
}
