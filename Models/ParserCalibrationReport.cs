namespace BDOLootTracker.Models;

public sealed class ParserCalibrationReport
{
    public int SchemaVersion { get; set; } = 2;
    public string AppVersion { get; set; } = string.Empty;
    public string BaseOfficialVersion { get; set; } = string.Empty;
    public bool CalibrationPassed { get; set; }
    public bool DiffersFromOfficial { get; set; }
    public int MobSampleCount { get; set; }
    public double MobConfidence { get; set; }
    public int GroundLootCheckCount { get; set; }
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
