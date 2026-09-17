namespace BDOLootTracker.Services;

public static class CommunityParserEndpoint
{
    // Owner-operated production Community Parser service. This is intentionally
    // built into the client so end users never need to configure a URL.
    public const string ProductionBaseUrl =
        "https://bdoloottracker-community-parser.bdoloottracker.workers.dev";

    public static string GetConfiguredBaseUrl() => ProductionBaseUrl;

    public static string NormalizeBaseUrl(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            return string.Empty;

        bool secure = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        bool localDev = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);

        if (!secure && !localDev)
            return string.Empty;

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static bool TryGetBaseUri(out Uri? baseUri)
    {
        string configured = GetConfiguredBaseUrl();
        if (string.IsNullOrWhiteSpace(configured) ||
            !Uri.TryCreate(configured + "/", UriKind.Absolute, out Uri? parsed))
        {
            baseUri = null;
            return false;
        }

        baseUri = parsed;
        return true;
    }

    public static Uri BuildUri(Uri baseUri, string relativePath)
        => new(baseUri, relativePath.TrimStart('/'));
}
