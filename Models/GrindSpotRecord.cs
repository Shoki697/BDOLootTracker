namespace BDOLootTracker.Models;

public sealed class GrindSpotRecord
{
    public string SpotKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyCollection<uint> ItemIds { get; init; } = Array.Empty<uint>();
}
