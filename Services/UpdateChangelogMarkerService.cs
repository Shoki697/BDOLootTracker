using System.IO;
using System.Text.Json;

namespace BDOLootTracker.Services;

/// <summary>
/// Durable marker used to bridge the short process boundary created by
/// Velopack's ApplyUpdatesAndRestart. It is intentionally independent from
/// settings.json so the post-update changelog has a second recovery path.
/// </summary>
public sealed class UpdateChangelogMarkerService
{
    private readonly string _markerPath;

    public UpdateChangelogMarkerService()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BDOLootTracker");

        Directory.CreateDirectory(folder);
        _markerPath = Path.Combine(folder, "pending-update.json");
    }

    public void WritePending(string version)
    {
        string normalized = NormalizeVersion(version);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        try
        {
            string json = JsonSerializer.Serialize(new PendingUpdateMarker
            {
                TargetVersion = normalized,
                CreatedUtc = DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(_markerPath, json);
        }
        catch
        {
            // settings.PendingChangelogVersion remains the backup marker.
        }
    }

    public string ReadPendingVersion()
    {
        try
        {
            if (!File.Exists(_markerPath))
                return string.Empty;

            var marker = JsonSerializer.Deserialize<PendingUpdateMarker>(
                File.ReadAllText(_markerPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return NormalizeVersion(marker?.TargetVersion ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    public void ClearPending()
    {
        try
        {
            if (File.Exists(_markerPath))
                File.Delete(_markerPath);
        }
        catch
        {
            // A stale marker is safer than silently losing What's New. It will
            // simply be retried on the next launch if deletion fails.
        }
    }

    private static string NormalizeVersion(string value)
        => (value ?? string.Empty).Trim().TrimStart('v', 'V');

    private sealed class PendingUpdateMarker
    {
        public string TargetVersion { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
    }
}
