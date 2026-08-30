namespace BDOLootTracker.Models;

public sealed class CatalogItemRecord
{
    public uint ItemId { get; init; }
    public int Grade { get; init; }
    public int CategoryPrimary { get; init; }
    public int CategorySecondary { get; init; }
    public Dictionary<string, string> Names { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
