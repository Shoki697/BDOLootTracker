namespace BDOLootTracker.Models;

public sealed class GrindDataResult
{
    public IReadOnlyList<MarketPriceRecord> Prices { get; init; } = Array.Empty<MarketPriceRecord>();
    public IReadOnlyList<GrindSpotRecord> Spots { get; init; } = Array.Empty<GrindSpotRecord>();
}
