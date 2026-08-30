namespace BDOLootTracker.Models;

public sealed class AppSettings
{
    public string AdapterName { get; set; } = string.Empty;
    public string Region { get; set; } = "EU";
    public string ItemLanguage { get; set; } = "us";
    public int? CharacterClassType { get; set; }
    public string CharacterSpec { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    public string GarmothApiKey { get; set; } = string.Empty;

    // Remembers whether the right-side live loot list was collapsed.
    public bool LootPanelCollapsed { get; set; }
}
