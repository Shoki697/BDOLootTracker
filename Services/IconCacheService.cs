using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace BDOLootTracker.Services;

/// <summary>
/// Helyi item-icon cache.
///
/// A Garmoth grind adatbázisból eltárolt IconUrl az elsődleges forrás.
/// A Garmoth ikonok jellemzően WEBP formátumúak, ezért ImageSharp-pal
/// valódi PNG-vé konvertáljuk őket, mielőtt a WPF UI betölti.
///
/// Session közben egy item ikonja legfeljebb egyszer töltődik le;
/// utána a lokális PNG fájlt használjuk.
/// </summary>
public sealed class IconCacheService : IDisposable
{
    private readonly DatabaseService _database;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _locks = new();
    private readonly HashSet<uint> _failedThisRun = new();
    private readonly object _failedLock = new();

    public IconCacheService(DatabaseService database)
    {
        _database = database;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        // A Garmoth assets szerver browser-szerű User-Agenttel megbízhatóbban válaszol.
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://garmoth.com/");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/webp"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/png"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/*", 0.8));
    }

    public async Task<string?> EnsureIconAsync(
        uint itemId,
        CancellationToken cancellationToken = default)
    {
        lock (_failedLock)
        {
            if (_failedThisRun.Contains(itemId))
                return null;
        }

        string folder = Path.Combine(
            Path.GetDirectoryName(_database.DatabasePath) ?? AppContext.BaseDirectory,
            "icons");

        Directory.CreateDirectory(folder);
        string finalPath = Path.Combine(folder, $"{itemId}.png");

        if (IsValidLocalIcon(finalPath))
        {
            _database.SetLocalIconPath(itemId, finalPath);
            return finalPath;
        }

        // Korábbi verzió esetleg WEBP byte-okat mentett .png néven.
        // Ha ilyen van, töröljük és újra letöltjük/konvertáljuk.
        TryDeleteInvalidFile(finalPath);

        var gate = _locks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (IsValidLocalIcon(finalPath))
            {
                _database.SetLocalIconPath(itemId, finalPath);
                return finalPath;
            }

            TryDeleteInvalidFile(finalPath);

            string? databaseIconUrl = _database.GetIconUrl(itemId);

            // Fontos a sorrend:
            // 1. Garmothból/importból kapott konkrét icon URL
            // 2. Garmoth általános drop-item fallback
            // 3. Pearl market PNG fallback
            //
            // Distinct kell, mert a DB URL néha már valamelyik fallback.
            string[] urls = new[]
            {
                databaseIconUrl ?? string.Empty,
                DatabaseService.BuildGarmothFallbackIconUrl(itemId),
                DatabaseService.BuildPrimaryIconUrl(itemId)
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

            foreach (string url in urls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);

                    if (Uri.TryCreate(url, UriKind.Absolute, out var iconUri) &&
                        iconUri.Host.Contains("bdocodex.com", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.Referrer = new Uri("https://bdocodex.com/");
                    }
                    else if (Uri.TryCreate(url, UriKind.Absolute, out iconUri) &&
                             iconUri.Host.Contains("garmoth.com", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.Referrer = new Uri("https://garmoth.com/");
                    }

                    using HttpResponseMessage response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                    if (!response.IsSuccessStatusCode)
                        continue;

                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length < 32)
                        continue;

                    // WPF nem kezeli natívan a WEBP-t. ImageSharp felismeri a bejövő
                    // formátumot és valódi PNG-vé konvertálja.
                    using Image image = Image.Load(bytes);

                    string tempPath = finalPath + ".tmp";
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);

                    await image.SaveAsync(
                        tempPath,
                        new PngEncoder(),
                        cancellationToken);

                    File.Move(tempPath, finalPath, overwrite: true);

                    if (!IsValidLocalIcon(finalPath))
                    {
                        TryDeleteInvalidFile(finalPath);
                        continue;
                    }

                    _database.SetLocalIconPath(itemId, finalPath);
                    return finalPath;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Következő fallback URL.
                }
            }

            lock (_failedLock)
                _failedThisRun.Add(itemId);

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsValidLocalIcon(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 32)
                return false;

            return Image.Identify(path) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteInvalidFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Nem kritikus. A következő indításnál újra próbáljuk.
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();

        foreach (var gate in _locks.Values)
            gate.Dispose();
    }
}
