using System.IO;
using System.Text.Json;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace BDOLootTracker.Services;

public sealed class AppUpdateService
{
    private const string ConfigFileName = "update-source.json";

    public async Task CheckForUpdatesAsync(Window owner, bool showNoUpdateMessage = false)
    {
        UpdateSourceConfig? config = LoadConfig();
        if (config == null || string.IsNullOrWhiteSpace(config.RepositoryUrl))
            return;

        try
        {
            var source = new GithubSource(
                config.RepositoryUrl.TrimEnd('/'),
                accessToken: null,
                prerelease: config.IncludePrereleases);

            var manager = new UpdateManager(source);

            // Updates only work for a real Velopack install. Running directly
            // from Visual Studio/bin/publish is intentionally ignored.
            if (!manager.IsInstalled)
                return;

            UpdateInfo? update = await manager.CheckForUpdatesAsync();
            if (update == null)
            {
                if (showNoUpdateMessage)
                {
                    MessageBox.Show(
                        owner,
                        "You already have the latest version.",
                        "BDO Loot Tracker Update",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            string newVersion = update.TargetFullRelease.Version.ToString();
            string currentVersion = manager.CurrentVersion?.ToString() ?? "current";

            string notes = string.IsNullOrWhiteSpace(update.TargetFullRelease.NotesMarkdown)
                ? string.Empty
                : $"\n\nRelease notes:\n{TrimReleaseNotes(update.TargetFullRelease.NotesMarkdown, 900)}";

            MessageBoxResult result = MessageBox.Show(
                owner,
                $"A new BDO Loot Tracker version is available.\n\n" +
                $"Installed: {currentVersion}\n" +
                $"Available: {newVersion}" +
                notes +
                "\n\nDownload and install it now? The tracker will restart automatically.",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
                return;

            await manager.DownloadUpdatesAsync(update);
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            // Startup update checks must never prevent the tracker from opening.
            // Only manual checks should bother the user with network/update errors.
            if (showNoUpdateMessage)
            {
                MessageBox.Show(
                    owner,
                    $"Could not check for updates.\n\n{ex.Message}",
                    "Update Check Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
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
