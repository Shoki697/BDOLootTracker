using System.IO;
using System.Text.Json;
using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

public sealed class SettingsService
{
    private readonly string _folder;
    private readonly string _settingsPath;

    public SettingsService()
    {
        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BDOLootTracker");

        Directory.CreateDirectory(_folder);
        _settingsPath = Path.Combine(_folder, "settings.json");
    }

    public AppSettings Load()
    {
        AppSettings settings;

        if (File.Exists(_settingsPath))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath))
                           ?? new AppSettings();
            }
            catch
            {
                settings = new AppSettings();
            }
        }
        else
        {
            settings = new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(settings.DatabasePath))
            settings.DatabasePath = Path.Combine(_folder, "loottracker.db");

        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath) ?? _folder);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_settingsPath, json);
    }
}
