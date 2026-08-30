using System.Net.Http;
using System.Net.Http.Headers;
using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

public sealed class DatabaseFetchService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ItemCatalogApiService _catalogApi;
    private readonly MarketApiService _marketApi;
    private readonly BdoCodexFallbackService _fallbackApi;

    public DatabaseFetchService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("BDOLootTracker", "0.4"));

        _catalogApi = new ItemCatalogApiService(_httpClient);
        _marketApi = new MarketApiService(_httpClient);
        _fallbackApi = new BdoCodexFallbackService(_httpClient);
    }

    public async Task FetchOrUpdateAsync(
        DatabaseService database,
        string region,
        string language,
        IProgress<DatabaseFetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DatabaseFetchProgress
        {
            Percent = 5,
            Message = "Downloading item database..."
        });

        var catalog = await _catalogApi.DownloadCatalogAsync(cancellationToken);
        var catalogFetchedAt = DateTime.UtcNow;

        progress?.Report(new DatabaseFetchProgress
        {
            Percent = 36,
            Message = $"Importing item names ({catalog.Count:N0} items)..."
        });

        await Task.Run(
            () => database.UpsertItemCatalog(catalog, catalogFetchedAt),
            cancellationToken);

        string normalizedRegion = DatabaseService.NormalizeRegion(region);

        progress?.Report(new DatabaseFetchProgress
        {
            Percent = 58,
            Message = $"Downloading {normalizedRegion} Garmoth grind loot / price database..."
        });

        var marketProgress = new Progress<(int Done, int Total)>(_ =>
        {
            progress?.Report(new DatabaseFetchProgress
            {
                Percent = 78,
                Message = $"Processing {normalizedRegion} grind loot data..."
            });
        });

        var market = await _marketApi.DownloadMarketAsync(
            normalizedRegion,
            catalog,
            marketProgress,
            cancellationToken);

        var marketFetchedAt = DateTime.UtcNow;

        progress?.Report(new DatabaseFetchProgress
        {
            Percent = 88,
            Message = $"Saving loot prices / trash flags ({market.Count:N0} items)..."
        });

        await Task.Run(
            () => database.UpsertMarketPrices(market, normalizedRegion, marketFetchedAt),
            cancellationToken);

        progress?.Report(new DatabaseFetchProgress
        {
            Percent = 90,
            Message = "Downloading grind spot / drop reference..."
        });

        try
        {
            var spots = await _marketApi.DownloadGrindSpotsAsync(cancellationToken);

            progress?.Report(new DatabaseFetchProgress
            {
                Percent = 91,
                Message = $"Saving grind spot reference ({spots.Count:N0} spots)..."
            });

            await Task.Run(
                () => database.UpsertGrindSpots(spots, DateTime.UtcNow),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Spot detection is optional. Keep an older local mapping if the
            // reference endpoint is temporarily unavailable or changes shape.
            progress?.Report(new DatabaseFetchProgress
            {
                Percent = 91,
                Message = $"Spot reference update skipped: {ex.Message}"
            });
        }

        // Very new/event items can temporarily be missing from both the regular
        // item catalog and the Garmoth grind-drop dataset. We only resolve items
        // that the tracker has actually seen as Unknown, and only during the
        // explicit Database Fetch / Update action.
        var unresolvedIds = database.GetUnresolvedItemIds(100);
        if (unresolvedIds.Count > 0)
        {
            progress?.Report(new DatabaseFetchProgress
            {
                Percent = 92,
                Message = $"Resolving {unresolvedIds.Count:N0} new/event item(s)..."
            });

            int resolved = 0;
            for (int i = 0; i < unresolvedIds.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                uint itemId = unresolvedIds[i];
                SupplementalItemRecord? item = await _fallbackApi.TryResolveAsync(
                    itemId,
                    language,
                    cancellationToken);

                if (item != null)
                {
                    database.UpsertSupplementalItem(item, DateTime.UtcNow);
                    resolved++;
                }

                progress?.Report(new DatabaseFetchProgress
                {
                    Percent = 92 + (int)Math.Round(((i + 1) / (double)unresolvedIds.Count) * 7.0),
                    Message = $"Resolving new/event items: {i + 1}/{unresolvedIds.Count} (resolved {resolved})"
                });

                // Be gentle with the fallback source.
                if (i + 1 < unresolvedIds.Count)
                    await Task.Delay(150, cancellationToken);
            }
        }

        progress?.Report(new DatabaseFetchProgress
        {
            Percent = 100,
            Message = "Database update complete."
        });
    }

    public void Dispose() => _httpClient.Dispose();
}
