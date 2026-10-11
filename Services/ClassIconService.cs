using System.IO;
using System.Net.Http;
using BDOLootTracker.Models;
using SkiaSharp;

namespace BDOLootTracker.Services;

public sealed class ClassIconService : IDisposable
{
    private readonly DatabaseService _database;
    private readonly HttpClient _httpClient;
    private readonly string _iconFolder;

    public ClassIconService(DatabaseService database)
    {
        _database = database;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BDOLootTracker/0.13.4");

        _iconFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BDOLootTracker",
            "class_icons");

        Directory.CreateDirectory(_iconFolder);
    }

    public async Task<string?> EnsureIconAsync(CharacterClassOption characterClass, CancellationToken cancellationToken = default)
    {
        if (characterClass.ClassType < 0)
            return null;

        if (!string.IsNullOrWhiteSpace(characterClass.IconPath) && File.Exists(characterClass.IconPath))
            return characterClass.IconPath;

        string destination = Path.Combine(_iconFolder, $"class_{characterClass.ClassType}.png");
        if (File.Exists(destination))
        {
            characterClass.IconPath = destination;
            _database.UpdateClassIconPath(characterClass.ClassType, destination);
            return destination;
        }

        if (string.IsNullOrWhiteSpace(characterClass.IconUrl))
            return null;

        try
        {
            using var response = await _httpClient.GetAsync(characterClass.IconUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            using SKBitmap? source = SKBitmap.Decode(bytes);
            if (source == null || source.Width <= 0 || source.Height <= 0)
                return null;

            int square = Math.Min(source.Width, source.Height);
            int x = Math.Max(0, (source.Width - square) / 2);
            int y = Math.Max(0, (source.Height - square) / 2);

            using var output = new SKBitmap(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(output))
            {
                canvas.Clear(SKColors.Transparent);
                var srcRect = new SKRect(x, y, x + square, y + square);
                var dstRect = new SKRect(0, 0, 64, 64);
                canvas.DrawBitmap(source, srcRect, dstRect);
                canvas.Flush();
            }

            using SKImage image = SKImage.FromBitmap(output);
            using SKData? png = image.Encode(SKEncodedImageFormat.Png, 100);
            if (png == null)
                return null;

            await using (var fs = File.Create(destination))
            {
                png.SaveTo(fs);
                await fs.FlushAsync(cancellationToken);
            }

            characterClass.IconPath = destination;
            _database.UpdateClassIconPath(characterClass.ClassType, destination);
            return destination;
        }
        catch
        {
            // Class icons are cosmetic. The dropdown keeps working with the initials fallback.
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
