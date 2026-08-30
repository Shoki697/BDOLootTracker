using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

/// <summary>
/// Optional fallback resolver for very new/event items that are not yet present
/// in the regular item catalog or Garmoth grind-drop database.
///
/// Important: this service is used only during Database Fetch / Update, never
/// from the live packet/session path.
/// </summary>
public sealed class BdoCodexFallbackService
{
    private static readonly Regex TitleRegex = new(
        @"<title>\s*(?<name>.*?)\s*-\s*BDO\s+Codex\s*</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex IconRegex = new(
        """(?:(?:https?:)?//bdocodex\.com)?(?<path>/items/new_icon/[^"'<>\s?]+\.(?:webp|png))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public BdoCodexFallbackService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SupplementalItemRecord?> TryResolveAsync(
        uint itemId,
        string language,
        CancellationToken cancellationToken)
    {
        string normalizedLanguage = NormalizeCodexLanguage(language);

        // First try the selected language. If that page does not resolve,
        // fall back to English because the main UI already has an English-name fallback.
        var result = await TryResolveFromPageAsync(itemId, normalizedLanguage, cancellationToken);
        if (result != null || normalizedLanguage == "us")
            return result;

        return await TryResolveFromPageAsync(itemId, "us", cancellationToken);
    }

    private async Task<SupplementalItemRecord?> TryResolveFromPageAsync(
        uint itemId,
        string language,
        CancellationToken cancellationToken)
    {
        string url = $"https://bdocodex.com/{language}/item/{itemId}/";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept-Language", language == "us" ? "en-US,en;q=0.9" : language);

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
                return null;

            Match titleMatch = TitleRegex.Match(html);
            if (!titleMatch.Success)
                return null;

            string name = WebUtility.HtmlDecode(titleMatch.Groups["name"].Value).Trim();
            if (string.IsNullOrWhiteSpace(name) ||
                name.Contains("Page not found", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string iconUrl = string.Empty;
            Match iconMatch = IconRegex.Match(html);
            if (iconMatch.Success)
                iconUrl = "https://bdocodex.com" + iconMatch.Groups["path"].Value;

            return new SupplementalItemRecord
            {
                ItemId = itemId,
                Language = language,
                Name = name,
                IconUrl = iconUrl
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Supplemental lookup must never make the entire DB refresh fail.
            return null;
        }
    }

    private static string NormalizeCodexLanguage(string language)
    {
        string normalized = DatabaseService.NormalizeLanguage(language);

        return normalized switch
        {
            "us" => "us",
            "de" => "de",
            "fr" => "fr",
            "es" => "es",
            "pt" => "pt",
            "sp" => "pt",
            "ru" => "ru",
            "tr" => "tr",
            "kr" => "kr",
            "jp" => "jp",
            "th" => "th",
            "tw" => "tw",
            "cn" => "cn",
            "id" => "id",
            _ => "us"
        };
    }
}
