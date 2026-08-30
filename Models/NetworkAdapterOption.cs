namespace BDOLootTracker.Models;

public sealed class NetworkAdapterOption
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public override string ToString() => Description;
}
