namespace BDOLootTracker.Services;

public static class MarketTaxCalculator
{
    // Central Market base collection rate after the standard 35% transaction tax.
    public const decimal BaseCollectionRate = 0.65m;

    public static decimal GetFamilyFameBonus(int familyFame)
    {
        familyFame = Math.Max(0, familyFame);
        if (familyFame >= 7000)
            return 0.015m;
        if (familyFame >= 4000)
            return 0.010m;
        if (familyFame >= 1000)
            return 0.005m;
        return 0m;
    }

    public static decimal GetCollectionRate(bool valuePack, bool merchantRing, int familyFame)
    {
        decimal bonus = 0m;
        if (valuePack)
            bonus += 0.30m;
        if (merchantRing)
            bonus += 0.05m;
        bonus += GetFamilyFameBonus(familyFame);

        return BaseCollectionRate * (1m + bonus);
    }

    public static decimal ApplyToLootValue(decimal grossValue, bool isTrash, bool taxApplied, decimal collectionRate)
    {
        if (!taxApplied || isTrash || grossValue <= 0)
            return grossValue;

        return grossValue * Math.Clamp(collectionRate, 0m, 1m);
    }

    public static decimal GetDisplayPercent(decimal rate)
        => decimal.Round(Math.Clamp(rate, 0m, 1m) * 100m, 2, MidpointRounding.ToEven);

    public static string FormatPercent(decimal rate)
        => $"{GetDisplayPercent(rate):0.00}%";
}
