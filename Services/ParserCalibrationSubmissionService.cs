using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

public sealed class ParserCalibrationSubmissionService : IDisposable
{
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ParserCalibrationSubmissionService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BDOLootTracker-CommunityParser/0.12.7");
    }

    public static string CurrentAppVersion
    {
        get
        {
            Version? version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version == null)
                return "0.12.7";

            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public bool IsConfigured => CommunityParserEndpoint.TryGetBaseUri(out _);

    public string ConfiguredBaseUrl => CommunityParserEndpoint.GetConfiguredBaseUrl();

    public async Task<CommunityParserSubmissionResult> SubmitAsync(
        ParserCalibrationReport report,
        CancellationToken cancellationToken = default)
    {
        if (!CommunityParserEndpoint.TryGetBaseUri(out Uri? baseUri) || baseUri == null)
        {
            return new CommunityParserSubmissionResult
            {
                Success = false,
                Message = "The built-in Community parser service endpoint is unavailable."
            };
        }

        Uri endpoint = CommunityParserEndpoint.BuildUri(baseUri, "/community/submit");

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                endpoint,
                report,
                JsonOptions,
                cancellationToken);

            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            CommunityParserSubmissionResult? result = null;

            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    result = JsonSerializer.Deserialize<CommunityParserSubmissionResult>(payload, JsonOptions);
                }
                catch
                {
                    // Fall through to a generic HTTP message below.
                }
            }

            if (result != null)
            {
                if (!response.IsSuccessStatusCode)
                    result.Success = false;
                return result;
            }

            return new CommunityParserSubmissionResult
            {
                Success = response.IsSuccessStatusCode,
                Accepted = response.IsSuccessStatusCode,
                Message = response.IsSuccessStatusCode
                    ? "Calibration submitted to the Community parser service."
                    : $"Community service rejected the calibration (HTTP {(int)response.StatusCode})."
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommunityParserSubmissionResult
            {
                Success = false,
                Message = "Community parser submission timed out."
            };
        }
        catch (Exception ex)
        {
            return new CommunityParserSubmissionResult
            {
                Success = false,
                Message = $"Community parser submission failed: {ex.Message}"
            };
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
