namespace BDOLootTracker.Models;

public sealed record ParserDiagnosticsResult(
    bool Success,
    ParserProfile ActiveProfile,
    string ProfileSource,
    string RemoteProfileVersion,
    bool RemoteProfileAvailable,
    bool ProfileUpdated,
    string RemoteSampleVersion,
    bool NewSampleAvailable,
    string Message);
