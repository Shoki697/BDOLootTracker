using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using BDOLootTracker.Models;
using BDOLootTracker.Services;

namespace BDOLootTracker.Views;

public partial class ParserCalibrationWindow : Window
{
    private enum CalibrationStep
    {
        None,
        MobLoot,
        Storage,
        Market
    }

    private readonly string _adapterName;
    private readonly bool _exitLagMode;
    private readonly ParserProfileService _profileService;
    private readonly ParserCalibrationService _calibrationService = new();
    private readonly ParserCalibrationSubmissionService _submissionService = new();
    private readonly HashSet<uint> _knownLootItemIds;

    private ParserProfile _workingProfile;
    private CalibrationStep _activeStep;
    private bool _mobComplete;
    private bool _storageComplete;
    private bool _marketComplete;
    private int _mobSampleCount;
    private double _mobConfidence;
    private int _storageSampleCount;
    private double _storageConfidence;
    private int _marketSampleCount;
    private double _marketConfidence;
    private string _storageMarker = string.Empty;
    private string _marketMarker = string.Empty;
    private bool _officialComparisonComplete;
    private bool _matchesOfficial;

    public bool ProfileActivated { get; private set; }

    public ParserCalibrationWindow(
        string adapterName,
        string databasePath,
        bool exitLagMode,
        ParserProfileService profileService)
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

        // Fixed anchors used by the wizard itself.
        _knownLootItemIds.Add(1);
        _knownLootItemIds.Add(ParserCalibrationService.CalibrationItemId);

        RefreshHeader();
        Closing += ParserCalibrationWindow_Closing;
        Closed += (_, _) => _calibrationService.Dispose();
    }

    private void MobCapture_Click(object sender, RoutedEventArgs e)
        => ToggleCapture(CalibrationStep.MobLoot);

    private void StorageCapture_Click(object sender, RoutedEventArgs e)
        => ToggleCapture(CalibrationStep.Storage);

    private void MarketCapture_Click(object sender, RoutedEventArgs e)
        => ToggleCapture(CalibrationStep.Market);

    private void ToggleCapture(CalibrationStep step)
    {
        if (_activeStep == CalibrationStep.None)
        {
            try
            {
                _calibrationService.Start(_adapterName);
                _activeStep = step;
                SetCaptureUi(step, capturing: true);
                LiveStatusText.Text = step switch
                {
                    CalibrationStep.MobLoot => "Capturing… collect 5–10 normal mob loot events, then click Stop & analyze.",
                    CalibrationStep.Storage => "Capturing… withdraw Black Stone ×100 from Storage using a Maid, wait 2–3 seconds, then click Stop & analyze.",
                    CalibrationStep.Market => "Capturing… withdraw Black Stone ×100 from Central Market Warehouse using a Maid, wait 2–3 seconds, then click Stop & analyze.",
                    _ => "Capturing…"
                };
            }
            catch (Exception ex)
            {
                AppDialog.Show(ex.Message, "Manual Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return;
        }

        if (_activeStep != step)
            return;

        CalibrationCapture capture;
        try
        {
            capture = _calibrationService.Stop();
        }
        catch (Exception ex)
        {
            _activeStep = CalibrationStep.None;
            SetCaptureUi(step, capturing: false);
            AppDialog.Show(ex.Message, "Manual Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _activeStep = CalibrationStep.None;
        SetCaptureUi(step, capturing: false);
        Analyze(step, capture);
    }

    private async void Analyze(CalibrationStep step, CalibrationCapture capture)
    {
        try
        {
            switch (step)
            {
                case CalibrationStep.MobLoot:
                {
                    MobCalibrationResult result = _calibrationService.AnalyzeMobLoot(
                        capture,
                        _workingProfile,
                        _knownLootItemIds,
                        _exitLagMode);

                    if (!result.Success || result.Profile == null)
                    {
                        SetStageStatus(MobStatusText, false, result.Message);
                        LiveStatusText.Text = result.Message;
                        return;
                    }

                    _workingProfile = result.Profile;
                    _mobComplete = true;
                    _storageComplete = false;
                    _marketComplete = false;
                    _mobSampleCount = result.SampleCount;
                    _mobConfidence = result.Confidence;
                    _storageSampleCount = 0;
                    _storageConfidence = 0;
                    _marketSampleCount = 0;
                    _marketConfidence = 0;
                    _storageMarker = string.Empty;
                    _marketMarker = string.Empty;
                    ResetOfficialComparison();
                    StorageStatusText.Text = "Not calibrated";
                    MarketStatusText.Text = "Not calibrated";
                    StorageStatusText.Foreground = MarketStatusText.Foreground = AmberBrush();
                    string status = $"✓ {result.SampleCount} samples • confidence {result.Confidence:P0} • {result.Profile.Signature} • item +{result.Profile.ItemIdOffset} • qty +{result.Profile.QuantityOffset}";
                    SetStageStatus(MobStatusText, true, status);
                    LiveStatusText.Text = result.Message;
                    break;
                }

                case CalibrationStep.Storage:
                {
                    TransferCalibrationResult result = _calibrationService.AnalyzeTransfer(
                        capture,
                        _workingProfile,
                        ParserCalibrationService.CalibrationItemId,
                        ParserCalibrationService.CalibrationQuantity,
                        "Storage");

                    if (!result.Success || result.Profile == null)
                    {
                        SetStageStatus(StorageStatusText, false, result.Message);
                        LiveStatusText.Text = result.Message;
                        return;
                    }

                    _workingProfile = result.Profile;
                    _storageComplete = true;
                    _marketComplete = false;
                    _storageSampleCount = result.SampleCount;
                    _storageConfidence = result.Confidence;
                    _storageMarker = result.Marker;
                    _marketSampleCount = 0;
                    _marketConfidence = 0;
                    _marketMarker = string.Empty;
                    ResetOfficialComparison();
                    MarketStatusText.Text = "Not calibrated";
                    MarketStatusText.Foreground = AmberBrush();
                    SetStageStatus(StorageStatusText, true, $"✓ {result.Marker} • confidence {result.Confidence:P0}");
                    LiveStatusText.Text = result.Message;
                    break;
                }

                case CalibrationStep.Market:
                {
                    TransferCalibrationResult result = _calibrationService.AnalyzeTransfer(
                        capture,
                        _workingProfile,
                        ParserCalibrationService.CalibrationItemId,
                        ParserCalibrationService.CalibrationQuantity,
                        "Market");

                    if (!result.Success || result.Profile == null)
                    {
                        SetStageStatus(MarketStatusText, false, result.Message);
                        LiveStatusText.Text = result.Message;
                        return;
                    }

                    _workingProfile = result.Profile;
                    _marketComplete = true;
                    _marketSampleCount = result.SampleCount;
                    _marketConfidence = result.Confidence;
                    _marketMarker = result.Marker;
                    SetStageStatus(MarketStatusText, true, $"✓ {result.Marker} • confidence {result.Confidence:P0}");
                    LiveStatusText.Text = result.Message;
                    await RefreshOfficialComparisonAsync();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LiveStatusText.Text = ex.Message;
            AppDialog.Show(ex.Message, "Manual Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ActivateProfileButton.IsEnabled = _mobComplete && _storageComplete && _marketComplete;
            SubmitCalibrationButton.IsEnabled = CanSubmitCalibration();
            RefreshStageButtonAvailability();
            RefreshHeader();
        }
    }

    private void ActivateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!_mobComplete || !_storageComplete || !_marketComplete)
        {
            AppDialog.Show(
                "Complete Mob Loot, Storage and Market calibration first.",
                "Manual Calibration",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            _workingProfile = _profileService.ActivateLocalCalibrationProfile(_workingProfile);
            ProfileActivated = true;
            RefreshHeader();
            LiveStatusText.Text = $"Local parser {_workingProfile.ProfileVersion} activated. It will be used the next time tracking starts.";

            AppDialog.Show(
                "Local calibration activated. The new profile will be used the next time a tracking session starts.",
                "Manual Calibration",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Manual Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_activeStep != CalibrationStep.None)
            return;

        try
        {
            ParserProfile? restored = _profileService.RollbackToLastKnownGood();
            if (restored == null)
            {
                AppDialog.Show(
                    "No last-known-good parser profile is available yet.",
                    "Manual Calibration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _workingProfile = ParserCalibrationService.Clone(restored);
            _mobComplete = false;
            _storageComplete = false;
            _marketComplete = false;
            _mobSampleCount = 0;
            _storageSampleCount = 0;
            _marketSampleCount = 0;
            _storageMarker = string.Empty;
            _marketMarker = string.Empty;
            ResetOfficialComparison();
            ActivateProfileButton.IsEnabled = false;
            SubmitCalibrationButton.IsEnabled = false;
            RefreshStageButtonAvailability();
            MobStatusText.Text = "Not calibrated";
            StorageStatusText.Text = "Not calibrated";
            MarketStatusText.Text = "Not calibrated";
            MobStatusText.Foreground = StorageStatusText.Foreground = MarketStatusText.Foreground = AmberBrush();
            ProfileActivated = true;
            RefreshHeader();
            LiveStatusText.Text = $"Rolled back to {restored.ProfileVersion}.";
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Manual Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SubmitCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (!_mobComplete || !_storageComplete || !_marketComplete)
        {
            AppDialog.Show(
                "Complete Mob Loot, Storage and Market calibration successfully before submitting.",
                "Submit Calibration",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SubmitCalibrationButton.IsEnabled = false;
        try
        {
            // Always re-check GitHub immediately before submission. Another user may
            // have already published the same repair while this wizard was open.
            ParserCalibrationComparisonResult comparison = await _profileService.CompareCalibrationWithOfficialAsync(_workingProfile);
            ApplyOfficialComparison(comparison);

            if (!comparison.Success)
            {
                AppDialog.Show(
                    comparison.Message + "\n\nSubmission stays disabled until the official parser can be checked.",
                    "Submit Calibration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (comparison.MatchesOfficial)
            {
                AppDialog.Show(
                    "A matching official parser is already available. No submission is needed.",
                    "Submit Calibration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var report = new ParserCalibrationReport
            {
                AppVersion = ParserCalibrationSubmissionService.CurrentAppVersion,
                BaseOfficialVersion = comparison.OfficialVersion,
                AllStepsPassed = true,
                DiffersFromOfficial = true,
                CalibrationItemId = ParserCalibrationService.CalibrationItemId,
                CalibrationQuantity = ParserCalibrationService.CalibrationQuantity,
                MobSampleCount = _mobSampleCount,
                MobConfidence = _mobConfidence,
                StorageSampleCount = _storageSampleCount,
                StorageConfidence = _storageConfidence,
                MarketSampleCount = _marketSampleCount,
                MarketConfidence = _marketConfidence,
                StorageMarker = _storageMarker,
                MarketMarker = _marketMarker,
                Profile = ParserCalibrationService.Clone(_workingProfile)
            };

            _submissionService.OpenGitHubSubmission(report);
            AppDialog.Show(
                "GitHub opened with the validated calibration report pre-filled. Review it and submit the issue. After submission, the repository workflow validates it and automatically publishes a community parser candidate for other users.",
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

        ParserCalibrationComparisonResult result = await _profileService.CompareCalibrationWithOfficialAsync(_workingProfile);
        ApplyOfficialComparison(result);
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
            OfficialComparisonText.Text = $"⚠ Differs from current official parser • {result.OfficialVersion}\nAll three calibration steps passed, so this repair can be submitted as a community candidate.";
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
        => _activeStep == CalibrationStep.None &&
           _mobComplete && _storageComplete && _marketComplete &&
           _officialComparisonComplete && !_matchesOfficial;

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private void ParserCalibrationWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_activeStep == CalibrationStep.None)
            return;

        var result = AppDialog.Show(
            "Calibration capture is still running. Stop it and close?",
            "Manual Calibration",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _ = _calibrationService.Stop();
        _activeStep = CalibrationStep.None;
    }

    private void SetCaptureUi(CalibrationStep step, bool capturing)
    {
        if (capturing)
        {
            MobCaptureButton.IsEnabled = step == CalibrationStep.MobLoot;
            StorageCaptureButton.IsEnabled = step == CalibrationStep.Storage;
            MarketCaptureButton.IsEnabled = step == CalibrationStep.Market;
        }
        else
        {
            RefreshStageButtonAvailability();
        }

        RollbackButton.IsEnabled = !capturing;
        ActivateProfileButton.IsEnabled = !capturing && _mobComplete && _storageComplete && _marketComplete;
        SubmitCalibrationButton.IsEnabled = !capturing && CanSubmitCalibration();

        MobCaptureButton.Content = capturing && step == CalibrationStep.MobLoot ? "Stop & analyze" : "Start capture";
        StorageCaptureButton.Content = capturing && step == CalibrationStep.Storage ? "Stop & analyze" : "Start capture";
        MarketCaptureButton.Content = capturing && step == CalibrationStep.Market ? "Stop & analyze" : "Start capture";
    }

    private void RefreshStageButtonAvailability()
    {
        if (_activeStep != CalibrationStep.None)
            return;

        MobCaptureButton.IsEnabled = true;
        StorageCaptureButton.IsEnabled = _mobComplete;
        MarketCaptureButton.IsEnabled = _mobComplete && _storageComplete;
    }

    private void RefreshHeader()
    {
        ActiveProfileText.Text = $"Working profile: {_workingProfile.ProfileVersion}  •  port {_workingProfile.ServerPort}  •  signature {_workingProfile.Signature}";
        if (string.IsNullOrWhiteSpace(LiveStatusText.Text))
            LiveStatusText.Text = "Run the three guided steps after a weekly patch if normal loot or Maid withdrawals are being parsed incorrectly.";
    }

    private static void SetStageStatus(System.Windows.Controls.TextBlock target, bool success, string text)
    {
        target.Text = text;
        target.Foreground = success
            ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
            : new SolidColorBrush(Color.FromRgb(248, 113, 113));
    }

    private static Brush AmberBrush()
        => new SolidColorBrush(Color.FromRgb(251, 191, 36));
}
