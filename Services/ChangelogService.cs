using System.Reflection;
using System.Text.Json;
using System.IO;
using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

public sealed class ChangelogService
{
    public ChangelogEntry? GetForVersion(string version)
    {
        string normalized = (version ?? string.Empty).Trim().TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(x => x.EndsWith("Resources.changelog.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
                return null;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return null;

            var entries = JsonSerializer.Deserialize<Dictionary<string, ChangelogEntry>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (entries == null)
                return null;

            return entries.TryGetValue(normalized, out ChangelogEntry? entry) ? entry : null;
        }
        catch
        {
            // A missing/malformed changelog must never block application startup.
            return null;
        }
    }
}
