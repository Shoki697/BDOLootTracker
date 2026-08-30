namespace BDOLootTracker.Models;

public sealed class DatabaseFetchProgress
{
    public int Percent { get; init; }
    public string Message { get; init; } = string.Empty;
}
