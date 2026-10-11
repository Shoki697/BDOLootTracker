using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using SkiaSharp;

namespace BDOLootTracker.Services;

/// <summary>
/// Local item-icon cache.
///
/// Garmoth item icons are often WEBP. SkiaSharp decodes the downloaded image
/// and stores a real PNG so WPF can load it reliably.
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

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://garmoth.com/");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*", 0.8));
    }

    public async Task<string?> EnsureIconAsync(uint itemId, CancellationToken cancellationToken = default)
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

                    using SKBitmap? bitmap = SKBitmap.Decode(bytes);
                    if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                        continue;

                    using SKImage image = SKImage.FromBitmap(bitmap);
                    using SKData? png = image.Encode(SKEncodedImageFormat.Png, 100);
                    if (png == null)
                        continue;

                    string tempPath = finalPath + ".tmp";
                    TryDeleteInvalidFile(tempPath);

                    await using (var fs = File.Create(tempPath))
                    {
                        png.SaveTo(fs);
                        await fs.FlushAsync(cancellationToken);
                    }

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
                    // Try next fallback URL.
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

            using SKBitmap? bitmap = SKBitmap.Decode(path);
            return bitmap != null && bitmap.Width > 0 && bitmap.Height > 0;
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
            // Not critical. Retry on next run.
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        foreach (var gate in _locks.Values)
            gate.Dispose();
    }
}
