using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BDOLootTracker.Models;

public sealed class LootRowViewModel : INotifyPropertyChanged
{
    private ulong _quantity;
    private long _unitPrice;
    private string _name = string.Empty;
    private string? _iconPath;
    private bool _isTrash;
    private DateTime _lastLootedUtc;

    public uint ItemId { get; init; }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string? IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value);
    }

    public bool IsTrash
    {
        get => _isTrash;
        set => SetField(ref _isTrash, value);
    }


    public DateTime LastLootedUtc
    {
        get => _lastLootedUtc;
        set => SetField(ref _lastLootedUtc, value);
    }

    public ulong Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value) return;
            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QuantityText));
            OnPropertyChanged(nameof(TotalSilver));
            OnPropertyChanged(nameof(TotalSilverText));
        }
    }

    public long UnitPrice
    {
        get => _unitPrice;
        set
        {
            if (_unitPrice == value) return;
            _unitPrice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UnitPriceText));
            OnPropertyChanged(nameof(TotalSilver));
            OnPropertyChanged(nameof(TotalSilverText));
        }
    }

    public decimal TotalSilver => (decimal)Quantity * UnitPrice;
    public string QuantityText => $"x{Quantity:N0}";
    public string UnitPriceText => UnitPrice <= 0 ? "—" : $"{UnitPrice:N0}";
    public string TotalSilverText => TotalSilver <= 0 ? "—" : $"{TotalSilver:N0}";

    public void ApplyDefinition(ItemDefinition item)
    {
        Name = item.Name;
        IsTrash = item.IsTrash;
        UnitPrice = item.ItemId == 1 && item.UnitPrice == 0 ? 1 : item.UnitPrice;

        if (!string.IsNullOrWhiteSpace(item.LocalIconPath))
            IconPath = item.LocalIconPath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
