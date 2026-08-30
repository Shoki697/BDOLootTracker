namespace BDOLootTracker.Models;

public sealed class DatabaseHealth
{
    public bool HasCatalog { get; init; }
    public bool HasSelectedLanguage { get; init; }
    public DateTime? CatalogUpdatedUtc { get; init; }
    public DateTime? MarketUpdatedUtc { get; init; }
    public int ItemCount { get; init; }
    public int NameCount { get; init; }
    public int MarketPriceCount { get; init; }
    public int CachedIconCount { get; init; }

    public bool MarketIsStale(TimeSpan maxAge)
        => MarketUpdatedUtc == null || DateTime.UtcNow - MarketUpdatedUtc.Value > maxAge;
}
