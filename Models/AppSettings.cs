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

    // Optional in-game overlay.
    public bool OverlayEnabled { get; set; }
    public string OverlayMode { get; set; } = "Detailed";
    public double OverlayBackgroundOpacity { get; set; } = 0.85;
    public int OverlayMaxDisplayedItems { get; set; } = 8;
    public double OverlayLeft { get; set; } = 30;
    public double OverlayTop { get; set; } = 80;
    public double OverlayDetailedWidth { get; set; } = 390;
    public double OverlayDetailedHeight { get; set; } = 540;
    public double OverlayCompactWidth { get; set; } = 390;
    public double OverlayCompactHeight { get; set; } = 165;
}
