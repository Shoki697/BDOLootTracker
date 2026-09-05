using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

/// <summary>
/// Uploads a completed local loot-tracker session to Garmoth's external
/// grind-tracker API. No request is made during live packet tracking; this
/// service is used only when the user explicitly presses Upload to Garmoth.
/// </summary>
public sealed class GarmothUploadService : IDisposable
{
    private const string UploadUrl = "https://api.garmoth.com/api/external/grind-tracker/sessions/create";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly DatabaseService _database;

    private static readonly IReadOnlyDictionary<string, int> GarmothClassIds =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Berserker"] = 0,
            ["Ranger"] = 1,
            ["Sorceress"] = 2,
            ["Tamer"] = 3,
            ["Valkyrie"] = 4,
            ["Warrior"] = 5,
            ["Witch"] = 6,
            ["Wizard"] = 7,
            ["Musa"] = 8,
            ["Maehwa"] = 9,
            ["Ninja"] = 10,
            ["Kunoichi"] = 11,
            ["Dark Knight"] = 12,
            ["Striker"] = 13,
            ["Mystic"] = 14,
            ["Lahn"] = 15,
            ["Archer"] = 16,
            ["Shai"] = 17,
            ["Guardian"] = 18,
            ["Hashashin"] = 19,
            ["Nova"] = 20,
            ["Sage"] = 21,
            ["Corsair"] = 22,
            ["Drakania"] = 23,
            ["Woosa"] = 24,
            ["Maegu"] = 25,
            ["Scholar"] = 26,
            ["Dosa"] = 27,
            ["Deadeye"] = 28,
            ["Wukong"] = 29,
            ["Seraph"] = 30,
            // Garmoth added Agent after the reference Classes.json was published.
            // It follows the next sequential Garmoth class id.
            ["Agent"] = 31
        };

    public GarmothUploadService(DatabaseService database, HttpClient? httpClient = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _ownsHttpClient = httpClient == null;
    }

    public sealed record UploadResult(bool DropRateRequested, bool DropRateAccepted);

    public Task<UploadResult> UploadSessionAsync(
        string apiKey,
        SessionSummary session,
        IReadOnlyCollection<SessionLootHistoryRow> loot,
        CancellationToken cancellationToken)
        => UploadSessionAsync(apiKey, session, loot, dropRatePercent: null, cancellationToken: cancellationToken);

    public async Task<UploadResult> UploadSessionAsync(
        string apiKey,
        SessionSummary session,
        IReadOnlyCollection<SessionLootHistoryRow> loot,
        int? dropRatePercent,
        CancellationToken cancellationToken)
    {
        apiKey = apiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Garmoth API token is missing. Add it in Settings first.");

        if (session.SessionId <= 0)
            throw new InvalidOperationException("No session is selected.");

        if (string.IsNullOrWhiteSpace(session.ClassName))
            throw new InvalidOperationException("This session has no class selected. Garmoth requires a class for the upload.");

        if (!GarmothClassIds.TryGetValue(session.ClassName.Trim(), out int classId))
            throw new InvalidOperationException($"Garmoth class mapping is not available for '{session.ClassName}'.");

        int spec = GetGarmothSpec(session.ClassName, session.Spec);
        int grindSpotId = ResolveGrindSpotId(session);

        double elapsedSeconds = Math.Max(1, session.Duration.TotalSeconds);
        int minutes = Math.Max(1, (int)(elapsedSeconds / 60d));

        decimal totalDecimal = loot.Sum(x => (decimal)x.Quantity * Math.Max(0, x.UnitPrice));
        long totalSilver = ToSafeInt64(totalDecimal);
        long hourly = ToSafeInt64(totalDecimal / (decimal)elapsedSeconds * 3600m);

        var drops = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (SessionLootHistoryRow item in loot)
        {
            if (item.ItemId == 0 || item.Quantity == 0)
                continue;

            string key = $"{item.ItemId}_0";
            if (drops.TryGetValue(key, out ulong existing))
                drops[key] = existing + item.Quantity;
            else
                drops[key] = item.Quantity;
        }

        if (drops.Count == 0)
            throw new InvalidOperationException("The selected session does not contain any uploadable loot items.");

        var payload = new Dictionary<string, object?>
        {
            ["class_id"] = classId,
            ["spec"] = spec,
            ["grindspot_id"] = grindSpotId,
            ["minutes"] = minutes,
            ["hourly"] = hourly,
            ["total"] = totalSilver,
            ["global"] = false,
            ["drops"] = drops
        };

        // Garmoth's external grind-tracker endpoint currently accepts the session
        // without exposing a working Drop Rate field. Earlier builds tried a
        // `drop_rate` property, but successful HTTP responses silently ignored it.
        // Keep the user's value as local session metadata instead of claiming it
        // was transferred remotely.
        bool dropRateRequested = dropRatePercent is > 0;

        UploadHttpResult result = await SendPayloadAsync(apiKey, payload, cancellationToken);
        if (result.Success)
            return new UploadResult(dropRateRequested, false);

        ThrowUploadError(result.StatusCode, result.ReasonPhrase, result.Body);
        throw new InvalidOperationException("Garmoth upload failed.");
    }

    private async Task<UploadHttpResult> SendPayloadAsync(
        string apiKey,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, UploadUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("apiKey", apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", "BDOLootTracker/0.12.3");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new UploadHttpResult(
            response.IsSuccessStatusCode,
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            body);
    }

    private static void ThrowUploadError(int statusCode, string reasonPhrase, string? body)
    {
        string detail = TrimForError(body);
        string message = statusCode switch
        {
            401 or 403 => "Garmoth rejected the API token. Check the token in Settings and try again.",
            429 => "Garmoth rate-limited the upload. Wait a moment and try again.",
            _ => $"Garmoth upload failed: HTTP {statusCode} {reasonPhrase}."
        };

        if (!string.IsNullOrWhiteSpace(detail))
            message += $" Response: {detail}";

        throw new HttpRequestException(message);
    }

    private sealed record UploadHttpResult(bool Success, int StatusCode, string ReasonPhrase, string Body);

    private int ResolveGrindSpotId(SessionSummary session)
    {
        int? resolved = _database.TryResolveGarmothSpotId(session.SpotKey, session.SpotName);
        if (resolved is > 0)
        {
            // Upgrade older sessions that stored a slug-like key. Future uploads
            // then use the numeric id directly and never need resolution again.
            if (!string.Equals(session.SpotKey, resolved.Value.ToString(), StringComparison.Ordinal))
            {
                try
                {
                    _database.UpdateSessionSpot(
                        session.SessionId,
                        resolved.Value.ToString(),
                        session.SpotName);
                }
                catch
                {
                    // Upload can still continue; this cache upgrade is optional.
                }
            }

            return resolved.Value;
        }

        if (string.IsNullOrWhiteSpace(session.SpotName))
        {
            throw new InvalidOperationException(
                "This session has no detected grind spot. Garmoth requires a grind spot for the upload.");
        }

        throw new InvalidOperationException(
            $"Could not map '{session.SpotName}' to a local Garmoth grind spot id. " +
            "Open Settings, run Fetch / Update Database once, then try the upload again.");
    }

    private static int GetGarmothSpec(string className, string? specName)
    {
        string spec = specName?.Trim() ?? string.Empty;

        if (string.Equals(className, "Scholar", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (string.Equals(className, "Archer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "Shai", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "Deadeye", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "Wukong", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "Seraph", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "Agent", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (spec.Equals("Awakening", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (spec.Equals("Succession", StringComparison.OrdinalIgnoreCase) ||
            spec.Equals("Talent", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (spec.Equals("Ascension", StringComparison.OrdinalIgnoreCase))
            return 1;

        throw new InvalidOperationException(
            $"This session has no supported Garmoth spec for {className}. Select the correct spec in Settings before starting the session.");
    }

    private static long ToSafeInt64(decimal value)
    {
        if (value <= 0)
            return 0;
        if (value >= long.MaxValue)
            return long.MaxValue;
        return decimal.ToInt64(decimal.Round(value, 0, MidpointRounding.AwayFromZero));
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static string? ReadLocalizedName(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            return null;

        foreach (string key in new[] { "us", "en", "english" })
        {
            if (value.TryGetProperty(key, out JsonElement localized) && localized.ValueKind == JsonValueKind.String)
                return localized.GetString();
        }

        return null;
    }

    private static bool TryReadPositiveInt(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value) && value > 0;

        if (element.ValueKind == JsonValueKind.String)
            return int.TryParse(element.GetString(), out value) && value > 0;

        return false;
    }

    private static string NormalizeName(string value)
        => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string TrimForError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 500 ? text : text[..500] + "…";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
