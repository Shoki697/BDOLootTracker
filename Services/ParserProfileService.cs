using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BDOLootTracker.Models;

namespace BDOLootTracker.Services;

public sealed class ParserProfileService : IDisposable
{
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/Shoki697/BDOLootTracker/main/parser/manifest.json";

    private readonly HttpClient _httpClient;
    private readonly string _folder;
    private readonly string _activeProfilePath;
    private readonly string _lastKnownGoodPath;
    private readonly string _samplePath;
    private readonly string _sampleVersionPath;

    public ParserProfileService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BDOLootTracker-ParserRecovery/1.0");

        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BDOLootTracker",
            "parser");

        Directory.CreateDirectory(_folder);
        _activeProfilePath = Path.Combine(_folder, "active-profile.json");
        _lastKnownGoodPath = Path.Combine(_folder, "last-known-good.json");
        _samplePath = Path.Combine(_folder, "latest-sample.pcapng");
        _sampleVersionPath = Path.Combine(_folder, "sample-version.txt");
    }

    /// <summary>
    /// Local-only. This method intentionally performs no network access and is
    /// safe to call during application startup.
    /// </summary>
    public ParserProfile LoadActiveProfile(out string source)
    {
        ParserProfile embedded = LoadEmbeddedProfile();
        ParserProfile? local = TryLoadProfileFile(_activeProfilePath);

        // A newer application release may contain a parser hotfix that is newer
        // than a profile cached by an older install. Prefer that built-in profile
        // even without any network access.
        if (local == null || CompareProfileVersions(embedded.ProfileVersion, local.ProfileVersion) > 0)
        {
            source = "Built-in fallback";
            return embedded;
        }

        source = "Local active profile";
        return local;
    }

    public ParserProfile LoadActiveProfile()
        => LoadActiveProfile(out _);

    public ParserProfile? LoadLastKnownGood()
        => TryLoadProfileFile(_lastKnownGoodPath);

    /// <summary>
    /// Called when START is pressed. Checks GitHub for a newer JSON profile,
    /// validates its SHA-256, and activates it. Failures never prevent a session
    /// from starting with the existing local profile.
    /// </summary>
    public async Task<ParserDiagnosticsResult> EnsureLatestProfileAsync(CancellationToken cancellationToken = default)
    {
        ParserProfile active = LoadActiveProfile(out string source);

        try
        {
            ParserManifest manifest = await DownloadManifestAsync(cancellationToken);
            bool different = CompareProfileVersions(
                manifest.LatestProfileVersion,
                active.ProfileVersion) > 0;

            bool sampleAvailable = IsNewSample(manifest);
            if (!different)
            {
                return new ParserDiagnosticsResult(
                    true,
                    active,
                    source,
                    manifest.LatestProfileVersion,
                    false,
                    false,
                    manifest.SampleVersion,
                    sampleAvailable,
                    "Parser profile is current.");
            }

            ParserProfile remote = await DownloadAndValidateProfileAsync(manifest, cancellationToken);
            SaveProfile(remote, _activeProfilePath);
            SaveProfile(remote, _lastKnownGoodPath);

            return new ParserDiagnosticsResult(
                true,
                remote,
                "GitHub profile",
                manifest.LatestProfileVersion,
                true,
                true,
                manifest.SampleVersion,
                sampleAvailable,
                $"Parser profile updated to {remote.ProfileVersion}.");
        }
        catch (Exception ex)
        {
            return new ParserDiagnosticsResult(
                false,
                active,
                source,
                string.Empty,
                false,
                false,
                string.Empty,
                false,
                $"Remote parser check unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Explicit Diagnostics action. It checks remote metadata but does not
    /// modify the active profile.
    /// </summary>
    public async Task<ParserDiagnosticsResult> RunDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        ParserProfile active = LoadActiveProfile(out string source);

        try
        {
            ParserManifest manifest = await DownloadManifestAsync(cancellationToken);
            bool different = CompareProfileVersions(
                manifest.LatestProfileVersion,
                active.ProfileVersion) > 0;

            int cachedMatches = CountValidCandidatesInCachedSample(active);
            string sampleValidation = cachedMatches > 0
                ? $" Cached sample validation found {cachedMatches:N0} valid loot candidate(s)."
                : string.IsNullOrWhiteSpace(GetCachedSamplePath())
                    ? string.Empty
                    : " Cached sample did not produce a valid loot candidate with the active profile.";

            return new ParserDiagnosticsResult(
                true,
                active,
                source,
                manifest.LatestProfileVersion,
                different,
                false,
                manifest.SampleVersion,
                IsNewSample(manifest),
                (different
                    ? $"A newer parser profile is available: {manifest.LatestProfileVersion}."
                    : "Parser profile is current.") + sampleValidation);
        }
        catch (Exception ex)
        {
            return new ParserDiagnosticsResult(
                false,
                active,
                source,
                string.Empty,
                false,
                false,
                string.Empty,
                false,
                $"Remote diagnostics unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Explicit repair action. Re-downloads the current remote profile even if
    /// the version number matches, validates it, stores it as last-known-good,
    /// and downloads a newer pcapng diagnostic sample when the manifest offers one.
    /// </summary>
    public async Task<ParserDiagnosticsResult> AutoRepairAsync(CancellationToken cancellationToken = default)
    {
        ParserProfile before = LoadActiveProfile(out string source);

        try
        {
            ParserManifest manifest = await DownloadManifestAsync(cancellationToken);
            ParserProfile remote = await DownloadAndValidateProfileAsync(manifest, cancellationToken);

            SaveProfile(remote, _activeProfilePath);
            SaveProfile(remote, _lastKnownGoodPath);

            bool sampleAvailable = IsNewSample(manifest);
            if (sampleAvailable)
                await DownloadSampleAsync(manifest, cancellationToken);

            int cachedMatches = CountValidCandidatesInCachedSample(remote);
            string validation = cachedMatches > 0
                ? $" Cached sample validation found {cachedMatches:N0} valid loot candidate(s)."
                : string.IsNullOrWhiteSpace(GetCachedSamplePath())
                    ? string.Empty
                    : " The cached packet sample did not contain a valid candidate for this profile; a manually reviewed profile may still be required.";

            return new ParserDiagnosticsResult(
                true,
                remote,
                "GitHub profile (repaired)",
                manifest.LatestProfileVersion,
                true,
                true,
                manifest.SampleVersion,
                false,
                (sampleAvailable
                    ? $"Parser repaired with {remote.ProfileVersion}. A newer packet sample was also cached for diagnostics."
                    : $"Parser repaired with {remote.ProfileVersion}.") + validation);
        }
        catch (Exception ex)
        {
            ParserProfile? fallback = LoadLastKnownGood();
            if (fallback != null)
            {
                SaveProfile(fallback, _activeProfilePath);
                return new ParserDiagnosticsResult(
                    false,
                    fallback,
                    "Last-known-good rollback",
                    string.Empty,
                    false,
                    false,
                    string.Empty,
                    false,
                    $"Auto Repair could not download/validate a new profile. Rolled back to {fallback.ProfileVersion}. {ex.Message}");
            }

            return new ParserDiagnosticsResult(
                false,
                before,
                source,
                string.Empty,
                false,
                false,
                string.Empty,
                false,
                $"Auto Repair failed: {ex.Message}");
        }
    }

    public string GetLocalSampleVersion()
    {
        try
        {
            return File.Exists(_sampleVersionPath)
                ? File.ReadAllText(_sampleVersionPath).Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public string GetCachedSamplePath()
        => File.Exists(_samplePath) ? _samplePath : string.Empty;

    public int CountValidCandidatesInCachedSample(ParserProfile profile)
    {
        string path = GetCachedSamplePath();
        if (string.IsNullOrWhiteSpace(path))
            return 0;

        try
        {
            ValidateProfile(profile);
            byte[] bytes = File.ReadAllBytes(path);
            byte[] signature = ParseHex(profile.Signature);
            int count = 0;

            for (int signatureStart = 0;
                 signatureStart <= bytes.Length - signature.Length;
                 signatureStart++)
            {
                bool match = true;
                for (int j = 0; j < signature.Length; j++)
                {
                    if (bytes[signatureStart + j] == signature[j])
                        continue;

                    match = false;
                    break;
                }

                if (!match)
                    continue;

                int candidateStart = signatureStart - profile.SignatureOffset;
                if (candidateStart < 0)
                    continue;

                int requiredEnd = candidateStart + Math.Max(
                    profile.MinimumLength,
                    Math.Max(profile.ItemIdOffset + 4, profile.QuantityOffset + 8));
                if (requiredEnd > bytes.Length)
                    continue;

                int packetLength = 0;
                for (int i = 0; i < profile.PacketLengthBytes; i++)
                    packetLength |= bytes[candidateStart + profile.PacketLengthOffset + i] << (8 * i);

                if (packetLength < profile.MinimumLength || packetLength > profile.MaximumPacketLength)
                    continue;

                uint itemId = BitConverter.ToUInt32(bytes, candidateStart + profile.ItemIdOffset);
                ulong quantity = BitConverter.ToUInt64(bytes, candidateStart + profile.QuantityOffset);

                if (itemId == 0 || itemId > profile.MaxReasonableItemId ||
                    quantity == 0 || quantity > profile.MaxReasonableQuantity)
                    continue;

                count++;
                signatureStart += Math.Max(0, packetLength - profile.SignatureOffset - 1);
                if (count >= 100_000)
                    break;
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    public void MarkProfileAsLastKnownGood(ParserProfile profile)
    {
        try
        {
            ValidateProfile(profile);
            SaveProfile(profile, _lastKnownGoodPath);
        }
        catch
        {
            // Health bookkeeping must never interrupt tracking.
        }
    }

    private async Task<ParserManifest> DownloadManifestAsync(CancellationToken cancellationToken)
    {
        string json = await _httpClient.GetStringAsync(DefaultManifestUrl, cancellationToken);
        ParserManifest? manifest = JsonSerializer.Deserialize<ParserManifest>(json, JsonOptions());

        if (manifest == null || manifest.SchemaVersion != 1)
            throw new InvalidDataException("Parser manifest is missing or uses an unsupported schema.");

        if (string.IsNullOrWhiteSpace(manifest.LatestProfileVersion) ||
            string.IsNullOrWhiteSpace(manifest.ProfileUrl) ||
            string.IsNullOrWhiteSpace(manifest.ProfileSha256))
        {
            throw new InvalidDataException("Parser manifest is incomplete.");
        }

        return manifest;
    }

    private async Task<ParserProfile> DownloadAndValidateProfileAsync(
        ParserManifest manifest,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await _httpClient.GetByteArrayAsync(manifest.ProfileUrl, cancellationToken);
        VerifySha256(bytes, manifest.ProfileSha256, "parser profile");

        ParserProfile? profile = JsonSerializer.Deserialize<ParserProfile>(bytes, JsonOptions());
        if (profile == null)
            throw new InvalidDataException("Parser profile could not be decoded.");

        ValidateProfile(profile);

        if (!string.Equals(profile.ProfileVersion, manifest.LatestProfileVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Parser manifest/profile version mismatch.");

        return profile;
    }

    private async Task DownloadSampleAsync(ParserManifest manifest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest.SampleVersion) ||
            string.IsNullOrWhiteSpace(manifest.SampleUrl) ||
            string.IsNullOrWhiteSpace(manifest.SampleSha256))
            return;

        byte[] bytes = await _httpClient.GetByteArrayAsync(manifest.SampleUrl, cancellationToken);
        VerifySha256(bytes, manifest.SampleSha256, "packet sample");

        string temp = _samplePath + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        File.Move(temp, _samplePath, overwrite: true);
        File.WriteAllText(_sampleVersionPath, manifest.SampleVersion.Trim());
    }

    private bool IsNewSample(ParserManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.SampleVersion) ||
            string.IsNullOrWhiteSpace(manifest.SampleUrl) ||
            string.IsNullOrWhiteSpace(manifest.SampleSha256))
            return false;

        return !string.Equals(
            GetLocalSampleVersion(),
            manifest.SampleVersion.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifySha256(byte[] bytes, string expected, string description)
    {
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string wanted = (expected ?? string.Empty).Trim().ToLowerInvariant();

        if (actual != wanted)
            throw new InvalidDataException($"SHA-256 validation failed for {description}.");
    }

    private ParserProfile? TryLoadProfileFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            ParserProfile? profile = JsonSerializer.Deserialize<ParserProfile>(File.ReadAllText(path), JsonOptions());
            if (profile == null)
                return null;

            ValidateProfile(profile);
            return profile;
        }
        catch
        {
            return null;
        }
    }

    private static ParserProfile LoadEmbeddedProfile()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("Resources.parser-default.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new InvalidOperationException("Built-in parser profile resource is missing.");

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException("Built-in parser profile could not be opened.");

        ParserProfile? profile = JsonSerializer.Deserialize<ParserProfile>(stream, JsonOptions());
        if (profile == null)
            throw new InvalidDataException("Built-in parser profile is invalid.");

        ValidateProfile(profile);
        return profile;
    }

    private static void SaveProfile(ParserProfile profile, string path)
    {
        ValidateProfile(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string json = JsonSerializer.Serialize(profile, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    public static byte[] ParseHex(string value)
    {
        string compact = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray());
        if (compact.Length == 0 || compact.Length % 2 != 0)
            throw new InvalidDataException("Parser signature contains invalid hex data.");

        return Convert.FromHexString(compact);
    }

    public static void ValidateProfile(ParserProfile profile)
    {
        if (profile.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported parser profile schema.");

        if (string.IsNullOrWhiteSpace(profile.ProfileVersion))
            throw new InvalidDataException("Parser profile version is missing.");

        if (profile.ServerPort == 0)
            throw new InvalidDataException("Parser server port is invalid.");

        byte[] signature = ParseHex(profile.Signature);
        if (profile.SignatureOffset < 0 ||
            profile.PacketLengthOffset < 0 ||
            profile.PacketLengthBytes < 1 || profile.PacketLengthBytes > 4 ||
            profile.MaximumPacketLength < profile.MinimumLength ||
            profile.ItemIdOffset < 0 ||
            profile.QuantityOffset < 0 ||
            profile.MinimumLength < 1 ||
            profile.SuppressLookbackBytes < 0 || profile.SuppressLookbackBytes > 4096 ||
            profile.SuppressStateTimeoutMilliseconds < 0 || profile.SuppressStateTimeoutMilliseconds > 10000)
            throw new InvalidDataException("Parser offsets/framing are invalid.");

        int required = Math.Max(
            profile.SignatureOffset + signature.Length,
            Math.Max(
                profile.PacketLengthOffset + profile.PacketLengthBytes,
                Math.Max(profile.ItemIdOffset + 4, profile.QuantityOffset + 8)));

        if (profile.MinimumLength < required)
            throw new InvalidDataException("Parser minimum packet length is smaller than its configured fields.");

        foreach (string prefix in profile.SuppressIfPrecededBy ?? new List<string>())
            _ = ParseHex(prefix);
    }

    private static int CompareProfileVersions(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return 0;

        static int[] Numbers(string value)
        {
            var numbers = new List<int>();
            int current = 0;
            bool inNumber = false;

            foreach (char c in value ?? string.Empty)
            {
                if (char.IsDigit(c))
                {
                    inNumber = true;
                    current = current > 100_000_000 ? current : current * 10 + (c - '0');
                }
                else if (inNumber)
                {
                    numbers.Add(current);
                    current = 0;
                    inNumber = false;
                }
            }

            if (inNumber)
                numbers.Add(current);

            return numbers.ToArray();
        }

        int[] a = Numbers(left);
        int[] b = Numbers(right);
        int count = Math.Max(a.Length, b.Length);
        for (int i = 0; i < count; i++)
        {
            int av = i < a.Length ? a[i] : 0;
            int bv = i < b.Length ? b[i] : 0;
            int cmp = av.CompareTo(bv);
            if (cmp != 0)
                return cmp;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonSerializerOptions JsonOptions()
        => new() { PropertyNameCaseInsensitive = true };

    public void Dispose() => _httpClient.Dispose();
}
