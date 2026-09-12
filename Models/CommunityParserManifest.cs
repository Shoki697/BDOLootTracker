namespace BDOLootTracker.Models;

public sealed class CommunityParserManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string CandidateId { get; set; } = string.Empty;
    public string CandidateVersion { get; set; } = string.Empty;
    public string BaseOfficialVersion { get; set; } = string.Empty;
    public string ProfileUrl { get; set; } = string.Empty;
    public string ProfileSha256 { get; set; } = string.Empty;
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string SourceIssueUrl { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
