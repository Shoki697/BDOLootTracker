namespace BDOLootTracker.Models;

public sealed class ParserManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string LatestProfileVersion { get; set; } = string.Empty;
    public string ProfileUrl { get; set; } = string.Empty;
    public string ProfileSha256 { get; set; } = string.Empty;

    // Optional packet capture maintained on GitHub for larger protocol changes.
    // The application can detect/download a newer sample for Diagnostics, while
    // arbitrary protocol changes still require a compatible JSON profile.
    public string SampleVersion { get; set; } = string.Empty;
    public string SampleUrl { get; set; } = string.Empty;
    public string SampleSha256 { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
