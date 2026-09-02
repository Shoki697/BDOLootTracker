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
    public string OverlaySortBy { get; set; } = "Quantity";
    public bool OverlaySortDescending { get; set; } = true;
    public double OverlayLeft { get; set; } = 30;
    public double OverlayTop { get; set; } = 80;
    public double OverlayDetailedWidth { get; set; } = 390;
    public double OverlayDetailedHeight { get; set; } = 540;
    public double OverlayCompactWidth { get; set; } = 390;
    public double OverlayCompactHeight { get; set; } = 165;

    // Optional global shortcuts. "None" leaves the shortcut unregistered.
    public string StartStopHotkey { get; set; } = "None";
    public string OverlayHotkey { get; set; } = "None";

    // Used by the styled What's New dialog so each version is shown once.
    public string LastSeenChangelogVersion { get; set; } = string.Empty;
}
