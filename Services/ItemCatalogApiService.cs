using System.IO;
using System.Net.Http;
using System.Text.Json;
using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

public sealed class ItemCatalogApiService
{
    private const string CatalogUrl =
        "https://raw.githubusercontent.com/andreivreja/veliainn-market-resources/main/data/items_all.json";

    private readonly HttpClient _httpClient;

    public ItemCatalogApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<CatalogItemRecord>> DownloadCatalogAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            CatalogUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The item database JSON format is not the expected object.");

        var result = new List<CatalogItemRecord>(40000);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var element = property.Value;

            uint itemId = 0;
            if (element.TryGetProperty("id", out var idElement) && idElement.TryGetUInt32(out var parsedId))
                itemId = parsedId;
            else if (!uint.TryParse(property.Name, out itemId))
                continue;

            int grade = TryGetInt32(element, "grade");
            int primary = TryGetInt32(element, "category_primary");
            int secondary = TryGetInt32(element, "category_secondary");

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (element.TryGetProperty("locale_name", out var localeElement) &&
                localeElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var locale in localeElement.EnumerateObject())
                {
                    if (locale.Value.ValueKind != JsonValueKind.String)
                        continue;

                    var name = locale.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        names[DatabaseService.NormalizeLanguage(locale.Name)] = name;
                }
            }

            if (names.Count == 0)
                names["us"] = $"Item #{itemId}";

            result.Add(new CatalogItemRecord
            {
                ItemId = itemId,
                Grade = grade,
                CategoryPrimary = primary,
                CategorySecondary = secondary,
                Names = names
            });
        }

        if (result.Count < 1000)
            throw new InvalidDataException($"The item database returned suspiciously few records ({result.Count}).");

        return result;
    }

    private static int TryGetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;

        return 0;
    }
}
