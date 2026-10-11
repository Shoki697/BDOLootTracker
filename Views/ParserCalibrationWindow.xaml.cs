using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BDOLootTracker.Models;
using BDOLootTracker.Services;

namespace BDOLootTracker.Views;

public partial class ParserCalibrationWindow : Window
{
    private const int GrindTargetSamples = 30;

    private readonly string _adapterName;
    private readonly bool _exitLagMode;
    private readonly ParserProfileService _profileService;
    private readonly ParserCalibrationService _calibrationService = new();
    private readonly ParserCalibrationSubmissionService _submissionService = new();
    private readonly HashSet<uint> _knownLootItemIds;
    private readonly DispatcherTimer _progressTimer;

    private ParserProfile _workingProfile;
    private bool _capturing;
    private CalibrationStep _activeStep = CalibrationStep.None;
    private bool _grindComplete;
    private bool _marketComplete;
    private bool _storageComplete;
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
            HashSet<uint> highConfidence = database.GetCalibrationHighConfidenceLootItemIds();
            HashSet<uint> legacyMapped = database.GetCalibrationTrustedLootItemIds();
            _knownLootItemIds = highConfidence.Count >= 5 ? highConfidence : legacyMapped;
        }
        catch
        {
            _knownLootItemIds = new HashSet<uint>();
        }

        _knownLootItemIds.Remove(1);
        _knownLootItemIds.Add(ParserCalibrationService.CalibrationItemId);

        GrindProgressBar.Maximum = GrindTargetSamples;
        GrindProgressText.Text = $"0 / {GrindTargetSamples}";

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += GrindProgressTimer_Tick;

        RefreshHeader();
        RefreshStageUi();
        LiveStatusText.Text = $"Ready. Grind calibration uses {_knownLootItemIds.Count:N0} trusted item IDs as anchors. Complete the three steps in order.";

        Closing += ParserCalibrationWindow_Closing;
        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            _calibrationService.Dispose();
            _submissionService.Dispose();
        };
    }

    private void GrindCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing && _activeStep == CalibrationStep.Grind)
        {
            StopAndAnalyzeGrind(manualStop: true);
            return;
        }

        if (_capturing)
            return;

        try
        {
            _calibrationService.Start(_adapterName);
            _capturing = true;
            _activeStep = CalibrationStep.Grind;
            GrindProgressBar.Value = 0;
            GrindProgressText.Text = $"0 / {GrindTargetSamples}";
            GrindStatusText.Text = "Capturing ground loot…";
            GrindStatusText.Foreground = AmberBrush();
            LiveStatusText.Text = "Grind capture running. Kill monsters normally; Market / Storage / Maid should stay closed. Capture will stop automatically.";
            _progressTimer.Start();
            RefreshStageUi();
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GrindProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (!_capturing || _activeStep != CalibrationStep.Grind)
            return;

        try
        {
            CalibrationCapture snapshot = _calibrationService.SnapshotCurrent();
            int samples = _calibrationService.EstimateMobLootSampleCount(snapshot, _workingProfile, _knownLootItemIds);
            int shown = Math.Min(samples, GrindTargetSamples);
            GrindProgressBar.Value = shown;
            GrindProgressText.Text = $"{shown} / {GrindTargetSamples}";
            GrindStatusText.Text = samples > 0 ? $"Collecting… {samples} valid loot samples detected" : "Waiting for valid ground-loot samples…";

            if (samples >= GrindTargetSamples)
                StopAndAnalyzeGrind(manualStop: false);
        }
        catch (Exception ex)
        {
            LiveStatusText.Text = "Progress check: " + ex.Message;
        }
    }

    private async void StopAndAnalyzeGrind(bool manualStop)
    {
        _progressTimer.Stop();

        CalibrationCapture capture;
        try
        {
            capture = _calibrationService.Stop();
        }
        catch (Exception ex)
        {
            _capturing = false;
            _activeStep = CalibrationStep.None;
            RefreshStageUi();
            AppDialog.Show(ex.Message, "Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _capturing = false;
        _activeStep = CalibrationStep.None;
        RefreshStageUi();

        try
        {
            MobCalibrationResult result = _calibrationService.AnalyzeMobLoot(capture, _workingProfile, _knownLootItemIds, _exitLagMode);
            if (!result.Success || result.Profile == null)
            {
                _grindComplete = false;
                GrindStatusText.Text = result.Message;
                GrindStatusText.Foreground = ErrorBrush();
                LiveStatusText.Text = manualStop
                    ? result.Message
                    : "Automatic Grind capture reached its target, but validation failed. " + result.Message;
                RefreshStageUi();
                return;
            }

            _workingProfile = result.Profile;
            _grindComplete = true;
            _marketComplete = false;
            _storageComplete = false;
            _mobSampleCount = result.SampleCount;
            _mobConfidence = result.Confidence;
            ResetOfficialComparison();

            GrindProgressBar.Value = GrindTargetSamples;
            GrindProgressText.Text = $"{Math.Min(result.SampleCount, GrindTargetSamples)} / {GrindTargetSamples}";
            GrindStatusText.Text = $"✓ Complete • {result.SampleCount} samples • confidence {result.Confidence:P0}";
            GrindStatusText.Foreground = SuccessBrush();
            MarketStatusText.Text = "Ready — withdraw exactly 100 Black Stones from Market Storage";
            StorageStatusText.Text = "Ready — complete Market first";
            LiveStatusText.Text = "Grind Loot baseline learned. Next: Market transfer calibration.";
            RefreshHeader();
            RefreshStageUi();

            await RefreshOfficialComparisonAsync();
        }
        catch (Exception ex)
        {
            GrindStatusText.Text = ex.Message;
            GrindStatusText.Foreground = ErrorBrush();
            LiveStatusText.Text = ex.Message;
            RefreshStageUi();
        }
    }

    private void MarketCapture_Click(object sender, RoutedEventArgs e)
        => HandleTransferCapture(CalibrationStep.Market);

    private void StorageCapture_Click(object sender, RoutedEventArgs e)
        => HandleTransferCapture(CalibrationStep.Storage);

    private void HandleTransferCapture(CalibrationStep step)
    {
        if (_capturing && _activeStep == step)
        {
            StopAndAnalyzeTransfer(step);
            return;
        }

        if (_capturing || !_grindComplete)
            return;

        if (step == CalibrationStep.Storage && !_marketComplete)
        {
            AppDialog.Show("Complete Market calibration first.", "Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _calibrationService.Start(_adapterName);
            _capturing = true;
            _activeStep = step;

            if (step == CalibrationStep.Market)
            {
                MarketStatusText.Text = "Capturing… withdraw exactly 100 Black Stones, wait a moment, then click Stop & Analyze";
                MarketStatusText.Foreground = AmberBrush();
                LiveStatusText.Text = "Market capture running. Perform exactly one 100x Black Stone withdrawal from Central Market / Market Storage.";
            }
            else
            {
                StorageStatusText.Text = "Capturing… withdraw exactly 100 Black Stones, wait a moment, then click Stop & Analyze";
                StorageStatusText.Foreground = AmberBrush();
                LiveStatusText.Text = "Storage capture running. Perform exactly one 100x Black Stone withdrawal from normal Storage / Warehouse.";
            }

            RefreshStageUi();
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopAndAnalyzeTransfer(CalibrationStep step)
    {
        CalibrationCapture capture;
        try
        {
            capture = _calibrationService.Stop();
        }
        catch (Exception ex)
        {
            _capturing = false;
            _activeStep = CalibrationStep.None;
            RefreshStageUi();
            AppDialog.Show(ex.Message, "Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _capturing = false;
        _activeStep = CalibrationStep.None;

        string label = step == CalibrationStep.Market ? "Market" : "Storage";
        TransferCalibrationResult result = _calibrationService.AnalyzeTransfer(
            capture,
            _workingProfile,
            ParserCalibrationService.CalibrationItemId,
            ParserCalibrationService.CalibrationQuantity,
            label);

        if (!result.Success || result.Profile == null)
        {
            TextBlockForStep(step).Text = result.Message;
            TextBlockForStep(step).Foreground = ErrorBrush();
            LiveStatusText.Text = result.Message;
            RefreshStageUi();
            return;
        }

        _workingProfile = result.Profile;
        if (step == CalibrationStep.Market)
        {
            _marketComplete = true;
            MarketStatusText.Text = $"✓ Complete • marker {result.Marker} • confidence {result.Confidence:P0}";
            MarketStatusText.Foreground = SuccessBrush();
            StorageStatusText.Text = "Ready — withdraw exactly 100 Black Stones from normal Storage / Warehouse";
            LiveStatusText.Text = "Market BLOCK marker learned. Next: Storage transfer calibration.";
        }
        else
        {
            _storageComplete = true;
            StorageStatusText.Text = $"✓ Complete • marker {result.Marker} • confidence {result.Confidence:P0}";
            StorageStatusText.Foreground = SuccessBrush();
            LiveStatusText.Text = "All calibration steps are complete. Save & Finish will activate the new local parser profile.";
        }

        RefreshHeader();
        RefreshStageUi();
    }

    private TextBlock TextBlockForStep(CalibrationStep step)
        => step == CalibrationStep.Market ? MarketStatusText : StorageStatusText;

    private void SaveFinish_Click(object sender, RoutedEventArgs e)
    {
        if (!_grindComplete || !_marketComplete || !_storageComplete || _capturing)
            return;

        try
        {
            ParserProfile activated = _profileService.ActivateLocalCalibrationProfile(_workingProfile);
            _workingProfile = ParserCalibrationService.Clone(activated);
            ProfileActivated = true;
            RefreshHeader();
            LiveStatusText.Text = $"Calibration saved. Local parser {activated.ProfileVersion} will be used when tracking starts.";
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            AppDialog.Show("Stop the current calibration capture before resetting.", "Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _workingProfile = ParserCalibrationService.Clone(_profileService.LoadActiveProfile());
        _grindComplete = false;
        _marketComplete = false;
        _storageComplete = false;
        _mobSampleCount = 0;
        _mobConfidence = 0;
        GrindProgressBar.Value = 0;
        GrindProgressText.Text = $"0 / {GrindTargetSamples}";
        GrindStatusText.Text = "Not calibrated";
        GrindStatusText.Foreground = AmberBrush();
        MarketStatusText.Text = "Waiting for Grind Loot calibration";
        MarketStatusText.Foreground = MutedBrush();
        StorageStatusText.Text = "Waiting for Grind Loot calibration";
        StorageStatusText.Foreground = MutedBrush();
        ResetOfficialComparison();
        RefreshHeader();
        RefreshStageUi();
        LiveStatusText.Text = "Calibration reset. Start again with Grind Loot.";
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
                AppDialog.Show("No last-known-good parser profile is available yet.", "Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _workingProfile = ParserCalibrationService.Clone(restored);
            _grindComplete = false;
            _marketComplete = false;
            _storageComplete = false;
            _mobSampleCount = 0;
            _mobConfidence = 0;
            ResetOfficialComparison();
            ProfileActivated = true;
            GrindProgressBar.Value = 0;
            GrindProgressText.Text = $"0 / {GrindTargetSamples}";
            GrindStatusText.Text = "Not calibrated";
            GrindStatusText.Foreground = AmberBrush();
            MarketStatusText.Text = "Waiting for Grind Loot calibration";
            StorageStatusText.Text = "Waiting for Grind Loot calibration";
            RefreshHeader();
            RefreshStageUi();
            LiveStatusText.Text = $"Rolled back to {restored.ProfileVersion}.";
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SubmitCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (!_grindComplete)
        {
            AppDialog.Show("Complete Grind Loot calibration before submitting.", "Submit Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_submissionService.IsConfigured)
        {
            AppDialog.Show("The built-in Community parser service is unavailable.", "Submit Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SubmitCalibrationButton.IsEnabled = false;
        try
        {
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

            AppDialog.Show(
                string.IsNullOrWhiteSpace(result.Message) ? "Calibration submitted to the Community parser service." : result.Message,
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
            OfficialComparisonText.Foreground = ErrorBrush();
        }
        else if (result.MatchesOfficial)
        {
            OfficialComparisonText.Text = $"✓ Matches current official parser • {result.OfficialVersion}\nNo parser submission is required.";
            OfficialComparisonText.Foreground = SuccessBrush();
        }
        else
        {
            string serviceState = _submissionService.IsConfigured
                ? $"Community service: {_submissionService.ConfiguredBaseUrl}"
                : "Community service is not configured.";
            OfficialComparisonText.Text = $"⚠ Differs from current official parser • {result.OfficialVersion}\n{serviceState}";
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
        => !_capturing && _grindComplete && _officialComparisonComplete && !_matchesOfficial && _submissionService.IsConfigured;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void ParserCalibrationWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_capturing)
            return;

        var result = AppDialog.Show("Calibration capture is still running. Stop it and close?", "Calibration", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _progressTimer.Stop();
        _ = _calibrationService.Stop();
        _capturing = false;
        _activeStep = CalibrationStep.None;
    }

    private void RefreshStageUi()
    {
        bool idle = !_capturing;

        GrindCaptureButton.Content = _capturing && _activeStep == CalibrationStep.Grind ? "Stop & Analyze" : "Start Grind Calibration";
        GrindCaptureButton.IsEnabled = idle || _activeStep == CalibrationStep.Grind;

        MarketCaptureButton.Content = _capturing && _activeStep == CalibrationStep.Market ? "Stop & Analyze" : "Start Market Capture";
        MarketCaptureButton.IsEnabled = (_grindComplete && idle) || _activeStep == CalibrationStep.Market;

        StorageCaptureButton.Content = _capturing && _activeStep == CalibrationStep.Storage ? "Stop & Analyze" : "Start Storage Capture";
        StorageCaptureButton.IsEnabled = (_grindComplete && _marketComplete && idle) || _activeStep == CalibrationStep.Storage;

        ResetCalibrationButton.IsEnabled = idle;
        RollbackButton.IsEnabled = idle;
        SaveFinishButton.IsEnabled = idle && _grindComplete && _marketComplete && _storageComplete;
        SubmitCalibrationButton.IsEnabled = CanSubmitCalibration();

        GrindTopStatus.Text = _grindComplete ? "✓ Grind Loot" : (_activeStep == CalibrationStep.Grind ? "● Grind Loot" : "○ Grind Loot");
        GrindTopStatus.Foreground = _grindComplete ? SuccessBrush() : (_activeStep == CalibrationStep.Grind ? AmberBrush() : MutedBrush());
        MarketTopStatus.Text = _marketComplete ? "✓ Market" : (_activeStep == CalibrationStep.Market ? "● Market" : "○ Market");
        MarketTopStatus.Foreground = _marketComplete ? SuccessBrush() : (_activeStep == CalibrationStep.Market ? AmberBrush() : MutedBrush());
        StorageTopStatus.Text = _storageComplete ? "✓ Storage" : (_activeStep == CalibrationStep.Storage ? "● Storage" : "○ Storage");
        StorageTopStatus.Foreground = _storageComplete ? SuccessBrush() : (_activeStep == CalibrationStep.Storage ? AmberBrush() : MutedBrush());
    }

    private void RefreshHeader()
    {
        string mode = _workingProfile.GroundLootOnly ? "ground-only + transfer blocklist" : "legacy";
        ActiveProfileText.Text = $"Working profile: {_workingProfile.ProfileVersion}  •  port {_workingProfile.ServerPort}  •  signature {_workingProfile.Signature}  •  {mode}";
    }

    private static Brush AmberBrush() => new SolidColorBrush(Color.FromRgb(251, 191, 36));
    private static Brush SuccessBrush() => new SolidColorBrush(Color.FromRgb(34, 197, 94));
    private static Brush ErrorBrush() => new SolidColorBrush(Color.FromRgb(248, 113, 113));
    private static Brush MutedBrush() => new SolidColorBrush(Color.FromRgb(148, 163, 184));

    private enum CalibrationStep
    {
        None,
        Grind,
        Market,
        Storage
    }
}
