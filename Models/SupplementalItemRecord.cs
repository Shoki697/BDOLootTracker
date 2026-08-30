namespace BDOLootTracker.Models;

public sealed class SupplementalItemRecord
{
    public uint ItemId { get; init; }
    public string Language { get; init; } = "us";
    public string Name { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;
}
