using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

public static class MarketTaxService
{
    public const decimal BaseCollectionRate = 0.65m;

    public static decimal GetFamilyFameBonus(int familyFame)
    {
        int fame = Math.Max(0, familyFame);
        if (fame >= 7000)
            return 0.015m;
        if (fame >= 4000)
            return 0.010m;
        if (fame >= 1000)
            return 0.005m;
        return 0m;
    }

    public static decimal GetMarketCollectionRate(bool valuePack, bool merchantRing, int familyFame)
    {
        decimal bonus = GetFamilyFameBonus(familyFame);
        if (valuePack)
            bonus += 0.30m;
        if (merchantRing)
            bonus += 0.05m;

        return BaseCollectionRate * (1m + bonus);
    }

    public static decimal GetMarketCollectionRate(AppSettings settings)
        => GetMarketCollectionRate(
            settings.TaxValuePackEnabled,
            settings.TaxMerchantRingEnabled,
            settings.TaxFamilyFame);

    public static long GetNetUnitPrice(long unitPrice, bool isTrash, bool applyTax, decimal marketCollectionRate)
    {
        if (!applyTax || isTrash || unitPrice <= 0)
            return Math.Max(0, unitPrice);

        decimal rate = Math.Clamp(marketCollectionRate, 0m, 1m);
        decimal netUnitPrice = decimal.Floor(unitPrice * rate);
        return netUnitPrice > long.MaxValue ? long.MaxValue : (long)netUnitPrice;
    }

    public static decimal ApplyToLootValue(long unitPrice, ulong quantity, bool isTrash, bool applyTax, decimal marketCollectionRate)
        => (decimal)GetNetUnitPrice(unitPrice, isTrash, applyTax, marketCollectionRate) * quantity;
}
