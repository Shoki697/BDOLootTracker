namespace BDOLootTracker.Models;

public sealed class CommunityParserSubmissionResult
{
    public bool Success { get; set; }
    public bool Accepted { get; set; }
    public bool AlreadyCurrent { get; set; }
    public string CandidateVersion { get; set; } = string.Empty;
    public string OfficialVersion { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
