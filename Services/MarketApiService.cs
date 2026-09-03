using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

/// <summary>
/// A grind loot / price adatokat a Garmoth publikus grind-tracker végpontjáról tölti le.
///
/// A MikeBrowni3 OCR Loot Tracker ugyanezt a végpontot használja:
/// https://api.garmoth.com/api/external/grind-tracker?region=eu&lang=us
///
/// A válasz "drops" része tartalmazza többek között az item ID-t, nevet,
/// árat, ikont, valamint trash / rare jelölést. A külön grind-spot referencia
/// végpontot a tracker csak Database Fetch közben tölti le, majd SQLite-ban
/// cache-eli a spot -> drop kapcsolatokat az offline spot felismeréshez.
/// </summary>
public sealed class MarketApiService
{
    private const string BaseUrl = "https://api.garmoth.com/api/external/grind-tracker";
    private const string GrindSpotsUrl = "https://api.garmoth.com/api/grind-tracker/getGrindSpots";
    private readonly HttpClient _httpClient;
    private List<GrindSpotRecord> _inlineSpots = new();

    public MarketApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<MarketPriceRecord>> DownloadMarketAsync(
        string region,
        IReadOnlyCollection<CatalogItemRecord> catalog,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        region = DatabaseService.NormalizeRegion(region);

        // A referenciaként használt tracker jelenlegi forrása explicit EU / NA régiót támogat.
        if (region is not ("EU" or "NA"))
        {
            throw new NotSupportedException(
                $"The Garmoth grind-tracker data source is currently configured only for EU and NA. Selected region: {region}");
        }

        string url = $"{BaseUrl}?region={region.ToLowerInvariant()}&lang=us";
        string json = await GetWithRetryAsync(url, region, cancellationToken);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        var inlineSpotBuffer = new List<GrindSpotRecord>();
        ParseSpotCollection(root, inlineSpotBuffer, null);
        _inlineSpots = NormalizeSpotRecords(inlineSpotBuffer);

        if (!root.TryGetProperty("drops", out JsonElement dropsElement) ||
            dropsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The Garmoth response does not contain a valid 'drops' object.");
        }

        // Nem szűrjük a drops rekordokat a spots listával.
        // A Garmothnál előfordulhat, hogy egy friss item már benne van a drops
        // adatbázisban, de a spot -> item kapcsolat még nincs ugyanabban a
        // pillanatban frissítve. Ilyenkor a régi szűrés pont az új trash itemet
        // dobta el, ezért minden érvényes drops rekordot lokálisan eltárolunk.
        var result = new List<MarketPriceRecord>(4096);

        foreach (JsonProperty property in dropsElement.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseMainKey(property.Name, out uint itemId) || itemId == 0)
                continue;

            JsonElement info = property.Value;
            if (info.ValueKind != JsonValueKind.Object)
                continue;

            long price = ReadInt64(info, "price");
            string name = ReadString(info, "name");
            string image = ReadString(info, "img");
            if (string.IsNullOrWhiteSpace(image))
                image = ReadString(info, "icon");

            string iconUrl;
            if (!string.IsNullOrWhiteSpace(image))
            {
                iconUrl = image.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? image
                    : $"https://assets.garmoth.com/img/{image.TrimStart('/')}";
            }
            else
            {
                iconUrl = $"https://assets.garmoth.com/img/new_icon/03_etc/04_dropitem/{itemId:D8}.webp";
            }

            result.Add(new MarketPriceRecord
            {
                ItemId = itemId,
                BasePrice = Math.Max(0, price),
                CurrentStock = 0,
                TotalTrades = 0,
                Name = name,
                IconUrl = iconUrl,
                IsTrash = ReadBoolLike(info, "trash"),
                IsRare = ReadBoolLike(info, "rare")
            });
        }

        progress?.Report((1, 1));

        if (result.Count < 100)
        {
            throw new InvalidDataException(
                $"The Garmoth {region} grind loot data source returned suspiciously few records ({result.Count}). " +
                "The existing price database will not be overwritten.");
        }

        return result
            .GroupBy(x => x.ItemId)
            .Select(g => g.First())
            .OrderBy(x => x.ItemId)
            .ToList();
    }

    public async Task<List<GrindSpotRecord>> DownloadGrindSpotsAsync(
        CancellationToken cancellationToken)
    {
        // IMPORTANT:
        // The same external grind-tracker response used for prices contains the
        // exact `spots` structure used by the reference OCR tracker:
        //   spots[spotId].name
        //   spots[spotId].items / drops -> main_key
        //
        // This feed is usually updated together with new grind loot. The older
        // getGrindSpots reference endpoint can lag behind newly released zones,
        // so we merge both sources and give the inline/external feed priority.
        var dedicated = new List<GrindSpotRecord>();

        try
        {
            string json = await GetSpotReferenceWithRetryAsync(cancellationToken);

            using JsonDocument document = JsonDocument.Parse(json);
            ParseSpotCollection(document.RootElement, dedicated, null);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Optional fallback source. The inline list from /external/grind-tracker
            // is enough on its own and should not make a database update fail.
        }

        var merged = NormalizeSpotRecords(_inlineSpots.Concat(dedicated));
        if (merged.Count >= 10)
            return merged;

        throw new InvalidDataException(
            $"The Garmoth grind spot reference returned suspiciously few usable spots ({merged.Count}). " +
            "The existing local spot database will not be overwritten.");
    }

    private static List<GrindSpotRecord> NormalizeSpotRecords(IEnumerable<GrindSpotRecord> spots)
    {
        return spots
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.ItemIds.Count > 0)
            .GroupBy(x => x.SpotKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new GrindSpotRecord
                {
                    SpotKey = first.SpotKey,
                    Name = first.Name,
                    ItemIds = group.SelectMany(x => x.ItemIds).Distinct().ToArray()
                };
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string> GetSpotReferenceWithRetryAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, GrindSpotsUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                    return body;

                string error =
                    $"Garmoth grind spot API error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                if (!string.IsNullOrWhiteSpace(body))
                    error += $" | Response: {TrimForError(body)}";

                bool transient =
                    (int)response.StatusCode == 408 ||
                    (int)response.StatusCode == 429 ||
                    (int)response.StatusCode >= 500;

                if (!transient || attempt == maxAttempts)
                    throw new HttpRequestException(error);

                lastException = new HttpRequestException(error);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException("Garmoth grind spot API timeout.");
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                if (attempt == maxAttempts)
                    throw;
            }

            int delayMs = attempt switch
            {
                1 => 600,
                2 => 1500,
                _ => 3000
            };

            await Task.Delay(delayMs, cancellationToken);
        }

        throw lastException ?? new HttpRequestException("Unknown Garmoth grind spot API error.");
    }

    private static void ParseSpotCollection(
        JsonElement element,
        List<GrindSpotRecord> output,
        string? fallbackKey)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement child in element.EnumerateArray())
            {
                if (TryBuildSpot(child, fallbackKey ?? $"spot_{index}", out GrindSpotRecord? spot) && spot is not null)
                    output.Add(spot);
                else
                    ParseSpotCollection(child, output, fallbackKey);

                index++;
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        // Common wrapper shapes used by JSON APIs.
        foreach (string wrapper in new[] { "data", "spots", "grind_spots", "grindSpots" })
        {
            if (element.TryGetProperty(wrapper, out JsonElement wrapped) &&
                wrapped.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                ParseSpotCollection(wrapped, output, null);
                return;
            }
        }

        if (TryBuildSpot(element, fallbackKey ?? string.Empty, out GrindSpotRecord? directSpot) && directSpot is not null)
        {
            output.Add(directSpot);
            return;
        }

        // Object keyed by spot id / slug.
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Object &&
                TryBuildSpot(property.Value, property.Name, out GrindSpotRecord? keyedSpot) &&
                keyedSpot is not null)
            {
                output.Add(keyedSpot);
            }
            else
            {
                ParseSpotCollection(property.Value, output, property.Name);
            }
        }
    }

    private static bool TryBuildSpot(
        JsonElement element,
        string fallbackKey,
        out GrindSpotRecord? spot)
    {
        spot = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        string name = ReadFirstString(element, "name", "spot_name", "spotName", "title", "label", "zone_name", "zoneName");
        if (string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(fallbackKey) &&
            fallbackKey.Any(char.IsLetter))
        {
            name = fallbackKey.Replace('_', ' ').Replace('-', ' ').Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
            return false;

        JsonElement dropContainer = default;
        bool hasDrops = false;
        foreach (string propertyName in new[]
                 {
                     "drops", "drop", "items", "loot", "loot_items", "lootItems",
                     "drop_items", "dropItems", "drop_list", "dropList",
                     "item_list", "itemList", "item_keys", "itemKeys"
                 })
        {
            if (element.TryGetProperty(propertyName, out dropContainer) &&
                dropContainer.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                hasDrops = true;
                break;
            }
        }

        if (!hasDrops)
            return false;

        var itemIds = new HashSet<uint>();
        ExtractItemIds(dropContainer, itemIds);
        if (itemIds.Count == 0)
            return false;

        string key = ReadFirstString(element, "id", "spot_id", "spotId", "key", "slug");
        if (string.IsNullOrWhiteSpace(key))
            key = fallbackKey;
        if (string.IsNullOrWhiteSpace(key))
            key = MakeStableSpotKey(name);

        spot = new GrindSpotRecord
        {
            SpotKey = key.Trim(),
            Name = name.Trim(),
            ItemIds = itemIds.ToArray()
        };

        return true;
    }

    private static void ExtractItemIds(JsonElement element, HashSet<uint> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                    ExtractItemIds(child, ids);
                break;

            case JsonValueKind.Object:
            {
                bool directFound = false;
                foreach (string propertyName in new[]
                         {
                             "main_key", "mainKey", "item_id", "itemId", "item_key", "itemKey", "mainkey"
                         })
                {
                    if (!element.TryGetProperty(propertyName, out JsonElement value))
                        continue;

                    if (TryReadItemId(value, out uint id))
                    {
                        ids.Add(id);
                        directFound = true;
                    }
                }

                // Some responses use dictionaries keyed by main_key.
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (TryParseMainKey(property.Name, out uint keyedId) && keyedId > 0)
                        ids.Add(keyedId);

                    if (!directFound && property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        ExtractItemIds(property.Value, ids);
                }
                break;
            }

            case JsonValueKind.String:
                if (TryParseMainKey(element.GetString(), out uint stringId))
                    ids.Add(stringId);
                break;

            case JsonValueKind.Number:
                if (element.TryGetUInt32(out uint numberId))
                    ids.Add(numberId);
                break;
        }
    }

    private static bool TryReadItemId(JsonElement element, out uint itemId)
    {
        itemId = 0;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetUInt32(out itemId) && itemId > 0;

        if (element.ValueKind == JsonValueKind.String)
            return TryParseMainKey(element.GetString(), out itemId) && itemId > 0;

        return false;
    }

    private static string ReadFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                string? text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (string languageKey in new[] { "us", "en", "english", "name" })
                {
                    if (value.TryGetProperty(languageKey, out JsonElement localized) &&
                        localized.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(localized.GetString()))
                    {
                        return localized.GetString()!;
                    }
                }
            }
        }

        return string.Empty;
    }

    private static string MakeStableSpotKey(string name)
    {
        var chars = name
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();

        string key = new string(chars);
        while (key.Contains("__", StringComparison.Ordinal))
            key = key.Replace("__", "_", StringComparison.Ordinal);

        return key.Trim('_');
    }

    private async Task<string> GetWithRetryAsync(
        string url,
        string region,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                string body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                    return body;

                string error =
                    $"Garmoth grind-tracker API error ({region}): HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                if (!string.IsNullOrWhiteSpace(body))
                    error += $" | Response: {TrimForError(body)}";

                bool transient =
                    (int)response.StatusCode == 408 ||
                    (int)response.StatusCode == 429 ||
                    (int)response.StatusCode >= 500;

                if (!transient || attempt == maxAttempts)
                    throw new HttpRequestException(error);

                lastException = new HttpRequestException(error);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException($"Garmoth grind-tracker API timeout ({region}).");
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                if (attempt == maxAttempts)
                    throw;
            }

            int delayMs = attempt switch
            {
                1 => 600,
                2 => 1500,
                _ => 3000
            };

            await Task.Delay(delayMs, cancellationToken);
        }

        throw lastException ?? new HttpRequestException("Unknown Garmoth API error.");
    }

    private static HashSet<uint> BuildUsedDropIdSet(JsonElement root)
    {
        var ids = new HashSet<uint>();

        if (!root.TryGetProperty("spots", out JsonElement spots) ||
            spots.ValueKind != JsonValueKind.Object)
        {
            return ids;
        }

        foreach (JsonProperty spotProperty in spots.EnumerateObject())
        {
            JsonElement spot = spotProperty.Value;
            if (spot.ValueKind != JsonValueKind.Object)
                continue;

            JsonElement items;
            if (spot.TryGetProperty("items", out items) && items.ValueKind == JsonValueKind.Array)
            {
                AddSpotItems(ids, items);
            }
            else if (spot.TryGetProperty("drops", out items) && items.ValueKind == JsonValueKind.Array)
            {
                AddSpotItems(ids, items);
            }
        }

        return ids;
    }

    private static void AddSpotItems(HashSet<uint> ids, JsonElement items)
    {
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (TryParseMainKey(item.GetString(), out uint id))
                    ids.Add(id);
            }
            else if (item.ValueKind == JsonValueKind.Number)
            {
                if (item.TryGetUInt32(out uint id))
                    ids.Add(id);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("main_key", out JsonElement mainKey))
                {
                    string? raw = mainKey.ValueKind switch
                    {
                        JsonValueKind.String => mainKey.GetString(),
                        JsonValueKind.Number => mainKey.GetRawText(),
                        _ => null
                    };

                    if (TryParseMainKey(raw, out uint id))
                        ids.Add(id);
                }
            }
        }
    }

    private static bool TryParseMainKey(string? raw, out uint itemId)
    {
        itemId = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string value = raw.Trim();
        int underscore = value.IndexOf('_');
        if (underscore >= 0)
            value = value[..underscore];

        return uint.TryParse(value, out itemId);
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return string.Empty;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;

        return 0;
    }

    private static bool ReadBoolLike(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return false;

        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.False)
            return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number != 0;
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? string.Empty;
            return text == "1" || bool.TryParse(text, out bool flag) && flag;
        }

        return false;
    }

    private static string TrimForError(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 450 ? text : text[..450] + "...";
    }
}
