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

    public const string CommunityManifestUrl =
        "https://raw.githubusercontent.com/Shoki697/BDOLootTracker/main/parser/community-manifest.json";

    private readonly HttpClient _httpClient;
    private readonly string _folder;
    private readonly string _activeProfilePath;
    private readonly string _lastKnownGoodPath;
    private readonly string _localCalibrationPath;
    private readonly string _communityBaseVersionPath;
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
        _localCalibrationPath = Path.Combine(_folder, "local-calibrated.json");
        _communityBaseVersionPath = Path.Combine(_folder, "community-base-version.txt");
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
        if (local == null || ShouldPreferOfficialProfile(embedded.ProfileVersion, local.ProfileVersion))
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
            bool different = ShouldInstallOfficialOverActive(manifest.LatestProfileVersion, active);

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
            ClearCommunityBaseVersion();

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
    /// Lightweight remote manifest check used by the Network button and the
    /// background parser-update detector. It never changes the active profile.
    /// </summary>
    public async Task<ParserDiagnosticsResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        ParserProfile active = LoadActiveProfile(out string source);

        try
        {
            ParserManifest manifest = await DownloadManifestAsync(cancellationToken);
            bool officialDifferent = ShouldInstallOfficialOverActive(manifest.LatestProfileVersion, active);

            if (officialDifferent)
            {
                return new ParserDiagnosticsResult(
                    true,
                    active,
                    source,
                    manifest.LatestProfileVersion,
                    true,
                    false,
                    manifest.SampleVersion,
                    IsNewSample(manifest),
                    $"A newer official parser profile is available: {manifest.LatestProfileVersion}.")
                {
                    RemoteProfileSource = "Official"
                };
            }

            CommunityParserManifest? communityManifest = await TryDownloadCommunityManifestAsync(cancellationToken);
            if (communityManifest != null &&
                !string.IsNullOrWhiteSpace(communityManifest.CandidateVersion) &&
                string.Equals(communityManifest.BaseOfficialVersion, manifest.LatestProfileVersion, StringComparison.OrdinalIgnoreCase))
            {
                ParserProfile candidate = await DownloadAndValidateCommunityProfileAsync(communityManifest, cancellationToken);
                if (!AreProfilesEquivalent(candidate, active))
                {
                    return new ParserDiagnosticsResult(
                        true,
                        active,
                        source,
                        communityManifest.CandidateVersion,
                        true,
                        false,
                        manifest.SampleVersion,
                        IsNewSample(manifest),
                        $"A community parser candidate is available: {communityManifest.CandidateVersion}.")
                    {
                        RemoteProfileSource = "Community"
                    };
                }
            }

            return new ParserDiagnosticsResult(
                true,
                active,
                source,
                manifest.LatestProfileVersion,
                false,
                false,
                manifest.SampleVersion,
                IsNewSample(manifest),
                "Parser profile is current.")
            {
                RemoteProfileSource = "Official"
            };
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

    public async Task<ParserDiagnosticsResult> InstallCommunityProfileAsync(CancellationToken cancellationToken = default)
    {
        ParserProfile before = LoadActiveProfile(out string source);

        try
        {
            ParserManifest officialManifest = await DownloadManifestAsync(cancellationToken);
            CommunityParserManifest? communityManifest = await TryDownloadCommunityManifestAsync(cancellationToken);
            if (communityManifest == null ||
                string.IsNullOrWhiteSpace(communityManifest.CandidateVersion) ||
                !string.Equals(communityManifest.BaseOfficialVersion, officialManifest.LatestProfileVersion, StringComparison.OrdinalIgnoreCase))
            {
                return new ParserDiagnosticsResult(
                    true, before, source, officialManifest.LatestProfileVersion, false, false,
                    officialManifest.SampleVersion, IsNewSample(officialManifest),
                    "No current community parser candidate is available.");
            }

            // An official update always wins. Community candidates are only used
            // while they are based on the exact current official parser version.
            if (ShouldInstallOfficialOverActive(officialManifest.LatestProfileVersion, before))
            {
                return await EnsureLatestProfileAsync(cancellationToken);
            }

            ParserProfile candidate = await DownloadAndValidateCommunityProfileAsync(communityManifest, cancellationToken);
            if (AreProfilesEquivalent(candidate, before))
            {
                return new ParserDiagnosticsResult(
                    true, before, source, communityManifest.CandidateVersion, false, false,
                    officialManifest.SampleVersion, IsNewSample(officialManifest),
                    "The community parser candidate is already active.")
                {
                    RemoteProfileSource = "Community"
                };
            }

            SaveProfile(before, _lastKnownGoodPath);
            SaveProfile(candidate, _activeProfilePath);
            SaveCommunityBaseVersion(communityManifest.BaseOfficialVersion);

            return new ParserDiagnosticsResult(
                true, candidate, "Community parser", communityManifest.CandidateVersion, true, true,
                officialManifest.SampleVersion, IsNewSample(officialManifest),
                $"Community parser {communityManifest.CandidateVersion} installed. Roll Back remains available if it behaves unexpectedly.")
            {
                RemoteProfileSource = "Community"
            };
        }
        catch (Exception ex)
        {
            return new ParserDiagnosticsResult(
                false, before, source, string.Empty, false, false, string.Empty, false,
                $"Community parser install failed: {ex.Message}")
            {
                RemoteProfileSource = "Community"
            };
        }
    }

    public async Task<OfficialParserSnapshot> GetLatestOfficialSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ParserManifest manifest = await DownloadManifestAsync(cancellationToken);
        ParserProfile profile = await DownloadAndValidateProfileAsync(manifest, cancellationToken);
        return new OfficialParserSnapshot(manifest, profile);
    }

    public async Task<ParserCalibrationComparisonResult> CompareCalibrationWithOfficialAsync(
        ParserProfile calibrated,
        CancellationToken cancellationToken = default)
    {
        try
        {
            OfficialParserSnapshot snapshot = await GetLatestOfficialSnapshotAsync(cancellationToken);
            bool matches = AreProfilesEquivalent(calibrated, snapshot.Profile);
            return new ParserCalibrationComparisonResult(
                true,
                matches,
                snapshot.Manifest.LatestProfileVersion,
                snapshot.Profile,
                matches
                    ? $"Matches the current official parser ({snapshot.Manifest.LatestProfileVersion})."
                    : $"Calibration differs from the current official parser ({snapshot.Manifest.LatestProfileVersion}).");
        }
        catch (Exception ex)
        {
            return new ParserCalibrationComparisonResult(
                false,
                false,
                string.Empty,
                null,
                $"Could not compare with the official parser: {ex.Message}");
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
            bool different = ShouldInstallOfficialOverActive(manifest.LatestProfileVersion, active);

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
            ClearCommunityBaseVersion();

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

    public ParserProfile ActivateLocalCalibrationProfile(ParserProfile profile)
    {
        ParserProfile activeBefore = LoadActiveProfile();
        ParserProfile local = CloneProfile(profile);
        local.ProfileVersion = BuildLocalCalibrationVersion(activeBefore.ProfileVersion);

        ValidateProfile(local);

        // Manual Calibration is intentionally reversible even when the user has
        // never downloaded a remote parser before. Snapshot the profile that was
        // active immediately before the local repair, then activate the new one.
        SaveProfile(activeBefore, _lastKnownGoodPath);
        SaveProfile(local, _localCalibrationPath);
        SaveProfile(local, _activeProfilePath);
        ClearCommunityBaseVersion();
        return local;
    }

    public ParserProfile? RollbackToLastKnownGood()
    {
        ParserProfile? fallback = LoadLastKnownGood();
        if (fallback == null)
            return null;

        SaveProfile(fallback, _activeProfilePath);
        ClearCommunityBaseVersion();
        return fallback;
    }

    public string GetLocalCalibrationPath()
        => _localCalibrationPath;

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

    private async Task<CommunityParserManifest?> TryDownloadCommunityManifestAsync(CancellationToken cancellationToken)
    {
        try
        {
            string json = await _httpClient.GetStringAsync(CommunityManifestUrl, cancellationToken);
            CommunityParserManifest? manifest = JsonSerializer.Deserialize<CommunityParserManifest>(json, JsonOptions());
            if (manifest == null || manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.CandidateVersion))
                return null;

            if (string.IsNullOrWhiteSpace(manifest.BaseOfficialVersion) ||
                string.IsNullOrWhiteSpace(manifest.ProfileUrl) ||
                string.IsNullOrWhiteSpace(manifest.ProfileSha256))
                return null;

            return manifest;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ParserProfile> DownloadAndValidateCommunityProfileAsync(
        CommunityParserManifest manifest,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await _httpClient.GetByteArrayAsync(manifest.ProfileUrl, cancellationToken);
        VerifySha256(bytes, manifest.ProfileSha256, "community parser profile");

        ParserProfile? profile = JsonSerializer.Deserialize<ParserProfile>(bytes, JsonOptions());
        if (profile == null)
            throw new InvalidDataException("Community parser profile could not be decoded.");

        ValidateProfile(profile);
        if (!string.Equals(profile.ProfileVersion, manifest.CandidateVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Community manifest/profile version mismatch.");

        return profile;
    }

    public static bool AreProfilesEquivalent(ParserProfile left, ParserProfile right)
    {
        if (left.SchemaVersion != right.SchemaVersion ||
            !string.Equals(left.Region?.Trim(), right.Region?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            left.ServerPort != right.ServerPort ||
            !string.Equals(NormalizeHexForComparison(left.Signature), NormalizeHexForComparison(right.Signature), StringComparison.OrdinalIgnoreCase) ||
            left.SignatureOffset != right.SignatureOffset ||
            left.PacketLengthOffset != right.PacketLengthOffset ||
            left.PacketLengthBytes != right.PacketLengthBytes ||
            left.MaximumPacketLength != right.MaximumPacketLength ||
            left.ItemIdOffset != right.ItemIdOffset ||
            left.QuantityOffset != right.QuantityOffset ||
            left.MinimumLength != right.MinimumLength ||
            left.MaxReasonableItemId != right.MaxReasonableItemId ||
            left.MaxReasonableQuantity != right.MaxReasonableQuantity ||
            left.SuppressLookbackBytes != right.SuppressLookbackBytes ||
            left.SuppressStateTimeoutMilliseconds != right.SuppressStateTimeoutMilliseconds)
        {
            return false;
        }

        string[] a = (left.SuppressIfPrecededBy ?? new List<string>())
            .Select(NormalizeHexForComparison)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] b = (right.SuppressIfPrecededBy ?? new List<string>())
            .Select(NormalizeHexForComparison)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeHexForComparison(string value)
    {
        try
        {
            return Convert.ToHexString(ParseHex(value));
        }
        catch
        {
            return string.Empty;
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

    private static ParserProfile CloneProfile(ParserProfile source)
        => new()
        {
            SchemaVersion = source.SchemaVersion,
            ProfileVersion = source.ProfileVersion,
            Region = source.Region,
            ServerPort = source.ServerPort,
            Signature = source.Signature,
            SignatureOffset = source.SignatureOffset,
            PacketLengthOffset = source.PacketLengthOffset,
            PacketLengthBytes = source.PacketLengthBytes,
            MaximumPacketLength = source.MaximumPacketLength,
            ItemIdOffset = source.ItemIdOffset,
            QuantityOffset = source.QuantityOffset,
            MinimumLength = source.MinimumLength,
            MaxReasonableItemId = source.MaxReasonableItemId,
            MaxReasonableQuantity = source.MaxReasonableQuantity,
            SuppressLookbackBytes = source.SuppressLookbackBytes,
            SuppressStateTimeoutMilliseconds = source.SuppressStateTimeoutMilliseconds,
            SuppressIfPrecededBy = new List<string>(source.SuppressIfPrecededBy ?? new List<string>())
        };

    private static string BuildLocalCalibrationVersion(string activeVersion)
    {
        DateTime now = DateTime.Now;
        int revision = 1;
        int[] activeNumbers = ExtractVersionNumbers(activeVersion);

        if (activeNumbers.Length >= 4 &&
            activeNumbers[0] == now.Year &&
            activeNumbers[1] == now.Month &&
            activeNumbers[2] == now.Day)
        {
            revision = Math.Max(1, activeNumbers[3] + 1);
        }

        return $"LOCAL-{now:yyyy.MM.dd}.{revision}";
    }

    private bool ShouldInstallOfficialOverActive(string officialVersion, ParserProfile active)
    {
        if (active.ProfileVersion.StartsWith("COMMUNITY-", StringComparison.OrdinalIgnoreCase))
        {
            string baseVersion = GetCommunityBaseVersion();
            if (!string.IsNullOrWhiteSpace(baseVersion))
                return !string.Equals(baseVersion, officialVersion, StringComparison.OrdinalIgnoreCase);
        }

        return ShouldPreferOfficialProfile(officialVersion, active.ProfileVersion);
    }

    private string GetCommunityBaseVersion()
    {
        try
        {
            return File.Exists(_communityBaseVersionPath)
                ? File.ReadAllText(_communityBaseVersionPath).Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void SaveCommunityBaseVersion(string version)
    {
        try
        {
            File.WriteAllText(_communityBaseVersionPath, version?.Trim() ?? string.Empty);
        }
        catch
        {
            // Sidecar metadata is only used to decide when an official parser
            // should supersede a temporary community candidate.
        }
    }

    private void ClearCommunityBaseVersion()
    {
        try
        {
            if (File.Exists(_communityBaseVersionPath))
                File.Delete(_communityBaseVersionPath);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static bool ShouldPreferOfficialProfile(string officialVersion, string activeVersion)
    {
        int cmp = CompareProfileVersions(officialVersion, activeVersion, numericOnly: true);
        if (cmp > 0)
            return true;

        // A manually calibrated profile deliberately wins over an older GitHub
        // profile. Once an approved remote profile reaches the same numeric
        // version, prefer the official one so one user's temporary local repair
        // does not remain pinned forever.
        return cmp == 0 && activeVersion.StartsWith("LOCAL-", StringComparison.OrdinalIgnoreCase);
    }

    private static int[] ExtractVersionNumbers(string value)
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

    private static int CompareProfileVersions(string left, string right, bool numericOnly = false)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return 0;

        int[] a = ExtractVersionNumbers(left);
        int[] b = ExtractVersionNumbers(right);
        int count = Math.Max(a.Length, b.Length);
        for (int i = 0; i < count; i++)
        {
            int av = i < a.Length ? a[i] : 0;
            int bv = i < b.Length ? b[i] : 0;
            int cmp = av.CompareTo(bv);
            if (cmp != 0)
                return cmp;
        }

        if (numericOnly)
            return 0;

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonSerializerOptions JsonOptions()
        => new() { PropertyNameCaseInsensitive = true };

    public void Dispose() => _httpClient.Dispose();
}
