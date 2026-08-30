namespace BDOLootTracker.Models;

public sealed class SessionLootHistoryRow
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? IconPath { get; init; }
    public ulong Quantity { get; init; }
    public long UnitPrice { get; init; }
    public bool IsTrash { get; init; }
    public bool IsIgnored { get; init; }

    public decimal TotalSilver => (decimal)Quantity * UnitPrice;
    public string QuantityText => $"x{Quantity:N0}";
    public string UnitPriceText => UnitPrice <= 0 ? "—" : $"{UnitPrice:N0}";
    public string TotalSilverText => TotalSilver <= 0 ? "—" : $"{TotalSilver:N0}";
}
