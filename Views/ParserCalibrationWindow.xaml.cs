using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using BDOLootTracker.Models;
using BDOLootTracker.Services;

namespace BDOLootTracker.Views;

public partial class ParserCalibrationWindow : Window
{
    private readonly string _adapterName;
    private readonly bool _exitLagMode;
    private readonly ParserProfileService _profileService;
    private readonly ParserCalibrationService _calibrationService = new();
    private readonly ParserCalibrationSubmissionService _submissionService = new();
    private readonly HashSet<uint> _knownLootItemIds;

    private ParserProfile _workingProfile;
    private bool _capturing;
    private bool _calibrationComplete;
    private int _mobSampleCount;
    private double _mobConfidence;
    private bool _officialComparisonComplete;
    private bool _matchesOfficial;

    public bool ProfileActivated { get; private set; }

    public ParserCalibrationWindow(string adapterName, string databasePath, bool exitLagMode, ParserProfileService profileService)
    {
        InitializeComponent();
        _adapterName = adapterName;
        _exitLagMode = exitLagMode;
        _profileService = profileService;
        _workingProfile = ParserCalibrationService.Clone(_profileService.LoadActiveProfile());

        try
        {
            var database = new DatabaseService(databasePath);
            _knownLootItemIds = database.GetGarmothKnownLootItemIds();
        }
        catch
        {
            _knownLootItemIds = new HashSet<uint>();
        }

        _knownLootItemIds.Add(1);
        _knownLootItemIds.Add(ParserCalibrationService.CalibrationItemId);

        RefreshHeader();
        Closing += ParserCalibrationWindow_Closing;
        Closed += (_, _) =>
        {
            _calibrationService.Dispose();
            _submissionService.Dispose();
        };
    }

    private void MobCapture_Click(object sender, RoutedEventArgs e)
    {
        if (!_capturing)
        {
            try
            {
                _calibrationService.Start(_adapterName);
                _capturing = true;
                SetCaptureUi(true);
                LiveStatusText.Text = "Capturing… collect 10–20 normal ground loot events only, then click Stop & analyze.";
            }
            catch (Exception ex)
            {
                AppDialog.Show(ex.Message, "Ground Loot Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        CalibrationCapture capture;
        try
        {
            capture = _calibrationService.Stop();
        }
        catch (Exception ex)
        {
            _capturing = false;
            SetCaptureUi(false);
            AppDialog.Show(ex.Message, "Ground Loot Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _capturing = false;
        SetCaptureUi(false);
        Analyze(capture);
    }

    private async void Analyze(CalibrationCapture capture)
    {
        try
        {
            MobCalibrationResult result = _calibrationService.AnalyzeMobLoot(capture, _workingProfile, _knownLootItemIds, _exitLagMode);
            if (!result.Success || result.Profile == null)
            {
                SetStageStatus(false, result.Message);
                LiveStatusText.Text = result.Message;
                return;
            }

            _workingProfile = result.Profile;
            _calibrationComplete = true;
            _mobSampleCount = result.SampleCount;
            _mobConfidence = result.Confidence;
            ResetOfficialComparison();

            SetStageStatus(true, $"✓ {result.SampleCount} samples • confidence {result.Confidence:P0} • ground-loot fingerprint learned");
            LiveStatusText.Text = result.Message;
            ActivateProfileButton.IsEnabled = true;
            RefreshHeader();

            await RefreshOfficialComparisonAsync();
        }
        catch (Exception ex)
        {
            SetStageStatus(false, ex.Message);
            LiveStatusText.Text = ex.Message;
        }
    }

    private void ActivateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!_calibrationComplete)
            return;

        try
        {
            ParserProfile activated = _profileService.ActivateLocalCalibrationProfile(_workingProfile);
            _workingProfile = ParserCalibrationService.Clone(activated);
            ProfileActivated = true;
            RefreshHeader();
            LiveStatusText.Text = $"Local ground-loot parser {activated.ProfileVersion} activated. It will be used the next time tracking starts.";
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Ground Loot Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
            return;

        try
        {
            ParserProfile? restored = _profileService.RollbackToLastKnownGood();
            if (restored == null)
            {
                AppDialog.Show("No last-known-good parser profile is available yet.", "Ground Loot Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _workingProfile = ParserCalibrationService.Clone(restored);
            _calibrationComplete = false;
            _mobSampleCount = 0;
            _mobConfidence = 0;
            ResetOfficialComparison();
            ActivateProfileButton.IsEnabled = false;
            MobStatusText.Text = "Not calibrated";
            MobStatusText.Foreground = AmberBrush();
            ProfileActivated = true;
            RefreshHeader();
            LiveStatusText.Text = $"Rolled back to {restored.ProfileVersion}.";
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Ground Loot Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SubmitCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (!_calibrationComplete)
        {
            AppDialog.Show("Complete the Ground Loot calibration successfully before submitting.", "Submit Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_submissionService.IsConfigured)
        {
            AppDialog.Show(
                "The built-in Community parser service is unavailable.",
                "Submit Calibration",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SubmitCalibrationButton.IsEnabled = false;
        try
        {
            // Re-fetch the official parser immediately before sending. If another
            // repair became official in the meantime, do not submit a duplicate.
            ParserCalibrationComparisonResult comparison = await _profileService.CompareCalibrationWithOfficialAsync(_workingProfile);
            ApplyOfficialComparison(comparison);

            if (!comparison.Success)
            {
                AppDialog.Show(comparison.Message + "\n\nSubmission stays disabled until the official parser can be checked.", "Submit Calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (comparison.MatchesOfficial)
            {
                AppDialog.Show("A matching official parser is already available. No submission is needed.", "Submit Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var report = new ParserCalibrationReport
            {
                AppVersion = ParserCalibrationSubmissionService.CurrentAppVersion,
                BaseOfficialVersion = comparison.OfficialVersion,
                CalibrationPassed = true,
                DiffersFromOfficial = true,
                MobSampleCount = _mobSampleCount,
                MobConfidence = _mobConfidence,
                GroundLootCheckCount = _workingProfile.GroundLootChecks?.Count ?? 0,
                Profile = ParserCalibrationService.Clone(_workingProfile)
            };

            CommunityParserSubmissionResult result = await _submissionService.SubmitAsync(report);
            if (!result.Success)
            {
                AppDialog.Show(result.Message, "Submit Calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result.AlreadyCurrent)
            {
                AppDialog.Show(
                    string.IsNullOrWhiteSpace(result.Message)
                        ? "An identical Community parser candidate is already available."
                        : result.Message,
                    "Submit Calibration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string candidate = string.IsNullOrWhiteSpace(result.CandidateVersion)
                ? string.Empty
                : $"\n\nCandidate: {result.CandidateVersion}";

            AppDialog.Show(
                (string.IsNullOrWhiteSpace(result.Message)
                    ? "Calibration submitted directly to the Community parser service. Other clients can detect the candidate automatically."
                    : result.Message) + candidate,
                "Submit Calibration",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Submit Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SubmitCalibrationButton.IsEnabled = CanSubmitCalibration();
        }
    }

    private async Task RefreshOfficialComparisonAsync()
    {
        ResetOfficialComparison();
        OfficialComparisonBorder.Visibility = Visibility.Visible;
        OfficialComparisonText.Text = "Checking the current official GitHub parser…";
        OfficialComparisonText.Foreground = AmberBrush();
        ApplyOfficialComparison(await _profileService.CompareCalibrationWithOfficialAsync(_workingProfile));
    }

    private void ApplyOfficialComparison(ParserCalibrationComparisonResult result)
    {
        OfficialComparisonBorder.Visibility = Visibility.Visible;
        _officialComparisonComplete = result.Success;
        _matchesOfficial = result.Success && result.MatchesOfficial;

        if (!result.Success)
        {
            OfficialComparisonText.Text = "⚠ " + result.Message + " Submission is disabled until the comparison succeeds.";
            OfficialComparisonText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
        else if (result.MatchesOfficial)
        {
            OfficialComparisonText.Text = $"✓ Matches current official parser • {result.OfficialVersion}\nNo parser submission is required.";
            OfficialComparisonText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        }
        else
        {
            string serviceState = _submissionService.IsConfigured
                ? $"Community service: {_submissionService.ConfiguredBaseUrl}"
                : "Community service is not configured. Set its URL in Settings > Network before submitting.";
            OfficialComparisonText.Text = $"⚠ Differs from current official parser • {result.OfficialVersion}\nGround-loot calibration passed. {serviceState}";
            OfficialComparisonText.Foreground = AmberBrush();
        }

        SubmitCalibrationButton.IsEnabled = CanSubmitCalibration();
    }

    private void ResetOfficialComparison()
    {
        _officialComparisonComplete = false;
        _matchesOfficial = false;
        SubmitCalibrationButton.IsEnabled = false;
        OfficialComparisonBorder.Visibility = Visibility.Collapsed;
        OfficialComparisonText.Text = string.Empty;
    }

    private bool CanSubmitCalibration()
        => !_capturing && _calibrationComplete && _officialComparisonComplete && !_matchesOfficial && _submissionService.IsConfigured;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ParserCalibrationWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_capturing)
            return;

        var result = AppDialog.Show("Calibration capture is still running. Stop it and close?", "Ground Loot Calibration", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _ = _calibrationService.Stop();
        _capturing = false;
    }

    private void SetCaptureUi(bool capturing)
    {
        MobCaptureButton.Content = capturing ? "Stop & analyze" : "Start capture";
        MobCaptureButton.IsEnabled = true;
        RollbackButton.IsEnabled = !capturing;
        ActivateProfileButton.IsEnabled = !capturing && _calibrationComplete;
        SubmitCalibrationButton.IsEnabled = !capturing && CanSubmitCalibration();
    }

    private void RefreshHeader()
    {
        string mode = _workingProfile.GroundLootOnly ? "ground-only" : "legacy";
        ActiveProfileText.Text = $"Working profile: {_workingProfile.ProfileVersion}  •  port {_workingProfile.ServerPort}  •  signature {_workingProfile.Signature}  •  {mode}";
        if (string.IsNullOrWhiteSpace(LiveStatusText.Text))
            LiveStatusText.Text = "After a weekly patch, calibrate using normal loot picked up from the ground. No Storage or Market calibration is required.";
    }

    private void SetStageStatus(bool success, string text)
    {
        MobStatusText.Text = text;
        MobStatusText.Foreground = success ? new SolidColorBrush(Color.FromRgb(34, 197, 94)) : new SolidColorBrush(Color.FromRgb(248, 113, 113));
    }

    private static Brush AmberBrush() => new SolidColorBrush(Color.FromRgb(251, 191, 36));
}
