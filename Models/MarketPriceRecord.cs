namespace BDOLootTracker.Models;

public sealed class MarketPriceRecord
{
    public uint ItemId { get; init; }
    public long BasePrice { get; init; }
    public long CurrentStock { get; init; }
    public long TotalTrades { get; init; }

    // Garmoth grind-tracker kiegészítő adatai.
    public string Name { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;
    public bool IsTrash { get; init; }
    public bool IsRare { get; init; }
}
