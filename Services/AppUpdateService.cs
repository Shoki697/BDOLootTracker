using System.IO;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace BDOLootTracker.Services;

public sealed class AppUpdateService
{
    private const string ConfigFileName = "update-source.json";

    public sealed record AvailableUpdateInfo(
        string CurrentVersion,
        string NewVersion,
        string ReleaseNotes);

    /// <summary>
    /// Performs a silent update check. It never shows UI and returns null when
    /// updates are unavailable, the app is not a Velopack install, or the check
    /// cannot be completed.
    /// </summary>
    public async Task<AvailableUpdateInfo?> CheckForAvailableUpdateAsync()
    {
        UpdateSourceConfig? config = LoadConfig();
        if (config == null || string.IsNullOrWhiteSpace(config.RepositoryUrl))
            return null;

        try
        {
            var manager = CreateManager(config);

            // Running directly from Visual Studio/bin/publish is intentionally
            // ignored. Update UI is only meaningful for a real Velopack install.
            if (!manager.IsInstalled)
                return null;

            UpdateInfo? update = await manager.CheckForUpdatesAsync();
            if (update == null)
                return null;

            string newVersion = NormalizeVersion(update.TargetFullRelease.Version.ToString());
            string currentVersion = NormalizeVersion(manager.CurrentVersion?.ToString() ?? string.Empty);
            string notes = string.IsNullOrWhiteSpace(update.TargetFullRelease.NotesMarkdown)
                ? string.Empty
                : TrimReleaseNotes(update.TargetFullRelease.NotesMarkdown, 900);

            return new AvailableUpdateInfo(currentVersion, newVersion, notes);
        }
        catch
        {
            // Periodic checks must never interrupt loot tracking.
            return null;
        }
    }

    /// <summary>
    /// Re-checks GitHub, downloads the latest Velopack package and restarts the
    /// application. UI confirmation is intentionally handled by MainWindow.
    /// </summary>
    public async Task InstallLatestUpdateAsync()
    {
        UpdateSourceConfig? config = LoadConfig();
        if (config == null || string.IsNullOrWhiteSpace(config.RepositoryUrl))
            throw new InvalidOperationException("The update source is not configured for this installation.");

        var manager = CreateManager(config);
        if (!manager.IsInstalled)
            throw new InvalidOperationException("Automatic updates are only available in the installed version of BDO Loot Tracker.");

        UpdateInfo? update = await manager.CheckForUpdatesAsync();
        if (update == null)
            throw new InvalidOperationException("No newer version is currently available.");

        await manager.DownloadUpdatesAsync(update);
        manager.ApplyUpdatesAndRestart(update);
    }

    private static UpdateManager CreateManager(UpdateSourceConfig config)
    {
        var source = new GithubSource(
            config.RepositoryUrl.TrimEnd('/'),
            accessToken: null,
            prerelease: config.IncludePrereleases);

        return new UpdateManager(source);
    }

    private static UpdateSourceConfig? LoadConfig()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            if (!File.Exists(path))
                return null;

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UpdateSourceConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeVersion(string value)
        => (value ?? string.Empty).Trim().TrimStart('v', 'V');

    private static string TrimReleaseNotes(string notes, int maxLength)
    {
        string normalized = notes.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength] + "…";
    }

    private sealed class UpdateSourceConfig
    {
        public string RepositoryUrl { get; set; } = string.Empty;
        public bool IncludePrereleases { get; set; }
    }
}
