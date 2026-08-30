using System.IO;
using System.Net.Http;
using BDOLootTracker.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BDOLootTracker/0.6");

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

            await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var image = await Image.LoadAsync(sourceStream, cancellationToken);

            // The official Pearl CDN class symbol is a wide banner. Crop the centered square
            // where the class emblem is located, then scale it to a small local PNG.
            int square = Math.Min(image.Width, image.Height);
            int x = Math.Max(0, (image.Width - square) / 2);
            int y = Math.Max(0, (image.Height - square) / 2);

            image.Mutate(ctx => ctx
                .Crop(new Rectangle(x, y, square, square))
                .Resize(new ResizeOptions
                {
                    Size = new Size(64, 64),
                    Mode = ResizeMode.Pad
                }));

            await image.SaveAsync(destination, new PngEncoder(), cancellationToken);

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

    public void Dispose()
        => _httpClient.Dispose();
}
