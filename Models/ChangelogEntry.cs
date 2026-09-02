namespace BDOLootTracker.Models;

public sealed class ChangelogEntry
{
    public string Title { get; set; } = "What's New";
    public List<ChangelogChange> Changes { get; set; } = new();
}

public sealed class ChangelogChange
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
