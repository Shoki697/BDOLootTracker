namespace BDOLootTracker.Models;

public sealed class ParserCalibrationReport
{
    public int SchemaVersion { get; set; } = 1;
    public string AppVersion { get; set; } = string.Empty;
    public string BaseOfficialVersion { get; set; } = string.Empty;
    public bool AllStepsPassed { get; set; }
    public bool DiffersFromOfficial { get; set; }
    public uint CalibrationItemId { get; set; }
    public ulong CalibrationQuantity { get; set; }
    public int MobSampleCount { get; set; }
    public double MobConfidence { get; set; }
    public int StorageSampleCount { get; set; }
    public double StorageConfidence { get; set; }
    public int MarketSampleCount { get; set; }
    public double MarketConfidence { get; set; }
    public string StorageMarker { get; set; } = string.Empty;
    public string MarketMarker { get; set; } = string.Empty;
    public ParserProfile Profile { get; set; } = new();
}

public sealed record ParserCalibrationComparisonResult(
    bool Success,
    bool MatchesOfficial,
    string OfficialVersion,
    ParserProfile? OfficialProfile,
    string Message);

public sealed record OfficialParserSnapshot(
    ParserManifest Manifest,
    ParserProfile Profile);
