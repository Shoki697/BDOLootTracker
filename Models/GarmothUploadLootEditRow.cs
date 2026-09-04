using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BDOLootTracker.Models;

public sealed class GarmothUploadLootEditRow : INotifyPropertyChanged
{
    private string _quantityText = "0";

    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? IconPath { get; init; }
    public long UnitPrice { get; init; }
    public bool IsTrash { get; init; }

    public string QuantityText
    {
        get => _quantityText;
        set
        {
            if (_quantityText == value)
                return;

            _quantityText = value?.Trim() ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalSilverText));
        }
    }

    public bool TryGetQuantity(out ulong quantity)
        => ulong.TryParse((_quantityText ?? string.Empty).Replace(",", string.Empty).Replace(" ", string.Empty), out quantity);

    public string UnitPriceText => UnitPrice <= 0 ? "—" : $"{UnitPrice:N0}";

    public string TotalSilverText
    {
        get
        {
            if (!TryGetQuantity(out ulong quantity) || UnitPrice <= 0)
                return "—";

            decimal total = (decimal)quantity * UnitPrice;
            return $"{total:N0}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
