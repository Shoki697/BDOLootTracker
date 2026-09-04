using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BDOLootTracker.Models;
using BDOLootTracker.Services;
using BDOLootTracker.Views;
using Forms = System.Windows.Forms;

namespace BDOLootTracker;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window, INotifyPropertyChanged
{
    // Keep the executable/taskbar/shortcut icon, but hide the tiny icon from
    // the native title bar. The larger tracker logo is shown in the app UI.
    private const int GwlExStyle = -20;
    private const long WsExDlgModalFrame = 0x00000001L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WmHotKey = 0x0312;
    private const int StartStopHotkeyId = 0xB001;
    private const int OverlayHotkeyId = 0xB002;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private static readonly TimeSpan MarketMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(2);
    private const double ExpandedMinWidth = 980;
    // Compact mode should end almost exactly at the right edge of the
    // left session cards (440 px + window/content margins).
    private const double CollapsedMinWidth = 500;
    private const double CollapsedWindowWidth = 510;

    private readonly SettingsService _settingsService = new();
    private readonly AppUpdateService _appUpdateService = new();
    private readonly UpdateChangelogMarkerService _updateChangelogMarkerService = new();
    private readonly ParserProfileService _parserProfileService = new();
    private readonly CaptureService _captureService = new();
    private DatabaseService _database;
    private GarmothUploadService _garmothUploadService;
    private IconCacheService _iconCache;
    private AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _updateCheckTimer;
    private OverlayWindow? _overlayWindow;
    private Forms.NotifyIcon? _trayIcon;
    private HwndSource? _hwndSource;
    private IntPtr _mainHwnd;
    private bool _allowClose;
    private bool _closePromptOpen;
    private bool _isGarmothUploading;
    private bool _isCheckingForUpdate;
    private bool _isInstallingUpdate;
    private bool _startupUpdatePromptShown;
    private AppUpdateService.AvailableUpdateInfo? _availableUpdate;
    private ParserProfile _activeParserProfile = new();
    private bool _parserProfileConfirmedThisSession;
    private bool _parserRecoveryPromptShown;
    private DateTime _nextParserHealthCheckUtc = DateTime.MinValue;

    private readonly Dictionary<uint, LootRowViewModel> _rowsById = new();
    private readonly Dictionary<uint, ulong> _sessionLoot = new();
    private HashSet<uint> _ignoredItemIds = new();
    private HashSet<uint> _garmothKnownItemIds = new();

    private DateTime? _sessionStartedUtc;
    private TimeSpan _stoppedElapsed = TimeSpan.Zero;
    private long _sessionId;

    private readonly Dictionary<string, double> _spotScores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _spotNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _spotScoredItemIds = new();
    private string _detectedSpotKey = string.Empty;
    private string _detectedSpotName = string.Empty;

    // Last-resort mappings for very new trash loot where Garmoth's drop list
    // can be newer than its spot -> item relationship data. Keep this list
    // intentionally tiny; the normal SQLite spot mapping remains primary.
    private static readonly IReadOnlyDictionary<uint, (string Key, string Name)> KnownTrashSpotFallbacks =
        new Dictionary<uint, (string Key, string Name)>
        {
            // Branch of Abundance (155,127 silver) - Aphrodon Temple.
            [980127] = ("213", "Aphrodon Temple")
        };

    private bool _lootPanelCollapsed;
    private double _expandedWindowWidth = 1220;

    private string _sessionTimeText = "00:00:00";
    private string _totalSilverText = "0";
    private string _silverPerHourText = "0";
    private string _trashPerHourText = "—";
    private string _statusText = "Ready";
    private string _connectionText = "Stopped";
    private Brush _statusBrush = Brushes.Gray;
    private string? _headerClassIconPath;
    private string _headerClassText = "Class not set";
    private string _headerSpotText = "Spot: —";
    private string _lootToggleText = "◀";
    private GridLength _lootSeparatorWidth = new(12);
    private GridLength _lootColumnWidth = new(1, GridUnitType.Star);
    private Visibility _lootPanelVisibility = Visibility.Visible;
    private Visibility _expandedHeaderVisibility = Visibility.Visible;
    private Visibility _compactHeaderVisibility = Visibility.Collapsed;
    private Brush _overlayButtonBackground = new SolidColorBrush(Color.FromRgb(20, 32, 42));
    private Brush _overlayButtonBorderBrush = new SolidColorBrush(Color.FromRgb(42, 62, 77));
    private string _updateBannerText = string.Empty;
    private Visibility _updateBannerVisibility = Visibility.Collapsed;

    public ObservableCollection<LootRowViewModel> LootRows { get; } = new();
    public string VersionText { get; } = GetVersionText();

    private void HideNativeTitleBarIcon()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle | WsExDlgModalFrame));

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch
        {
            // Cosmetic only. If Windows rejects the style change, the app
            // continues normally with its embedded application icon.
        }
    }

    private static string GetVersionText()
    {
        // Prefer the actual executable FileVersion. GitHub Actions writes this
        // from the release version (eg. 0.10.4.0), so the UI always reflects
        // the version that is really installed.
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                var raw = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                if (Version.TryParse(raw, out var fileVersion))
                    return $"v{fileVersion.Major}.{fileVersion.Minor}.{Math.Max(0, fileVersion.Build)}";
            }
        }
        catch
        {
            // Fall back to assembly metadata below.
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version == null)
            return "v0.0.0";

        return $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    public string SessionTimeText { get => _sessionTimeText; private set => SetField(ref _sessionTimeText, value); }
    public string TotalSilverText { get => _totalSilverText; private set => SetField(ref _totalSilverText, value); }
    public string SilverPerHourText { get => _silverPerHourText; private set => SetField(ref _silverPerHourText, value); }
    public string TrashPerHourText { get => _trashPerHourText; private set => SetField(ref _trashPerHourText, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string ConnectionText { get => _connectionText; private set => SetField(ref _connectionText, value); }
    public Brush StatusBrush { get => _statusBrush; private set => SetField(ref _statusBrush, value); }
    public string? HeaderClassIconPath { get => _headerClassIconPath; private set => SetField(ref _headerClassIconPath, value); }
    public string HeaderClassText { get => _headerClassText; private set => SetField(ref _headerClassText, value); }
    public string HeaderSpotText { get => _headerSpotText; private set => SetField(ref _headerSpotText, value); }
    public string LootToggleText { get => _lootToggleText; private set => SetField(ref _lootToggleText, value); }
    public GridLength LootSeparatorWidth { get => _lootSeparatorWidth; private set => SetField(ref _lootSeparatorWidth, value); }
    public GridLength LootColumnWidth { get => _lootColumnWidth; private set => SetField(ref _lootColumnWidth, value); }
    public Visibility LootPanelVisibility { get => _lootPanelVisibility; private set => SetField(ref _lootPanelVisibility, value); }
    public Visibility ExpandedHeaderVisibility { get => _expandedHeaderVisibility; private set => SetField(ref _expandedHeaderVisibility, value); }
    public Visibility CompactHeaderVisibility { get => _compactHeaderVisibility; private set => SetField(ref _compactHeaderVisibility, value); }
    public Brush OverlayButtonBackground { get => _overlayButtonBackground; private set => SetField(ref _overlayButtonBackground, value); }
    public Brush OverlayButtonBorderBrush { get => _overlayButtonBorderBrush; private set => SetField(ref _overlayButtonBorderBrush, value); }
    public string UpdateBannerText { get => _updateBannerText; private set => SetField(ref _updateBannerText, value); }
    public Visibility UpdateBannerVisibility { get => _updateBannerVisibility; private set => SetField(ref _updateBannerVisibility, value); }

    public IEnumerable<LootRowViewModel> MainLootRows => GetSortedLootRows();

    public IEnumerable<LootRowViewModel> OverlayLootRows
    {
        get
        {
            int maxItems = Math.Clamp(_settings?.OverlayMaxDisplayedItems ?? 8, 1, 20);
            return GetSortedLootRows().Take(maxItems);
        }
    }

    private IOrderedEnumerable<LootRowViewModel> GetSortedLootRows()
    {
        string sortBy = _settings?.OverlaySortBy ?? "Quantity";
        bool descending = _settings?.OverlaySortDescending ?? true;

        IOrderedEnumerable<LootRowViewModel> ordered = sortBy switch
        {
            "Last Looted" => descending
                ? LootRows.OrderByDescending(x => x.LastLootedUtc)
                : LootRows.OrderBy(x => x.LastLootedUtc),
            "Total Value" => descending
                ? LootRows.OrderByDescending(x => x.TotalSilver)
                : LootRows.OrderBy(x => x.TotalSilver),
            "Unit Price" => descending
                ? LootRows.OrderByDescending(x => x.UnitPrice)
                : LootRows.OrderBy(x => x.UnitPrice),
            _ => descending
                ? LootRows.OrderByDescending(x => x.Quantity)
                : LootRows.OrderBy(x => x.Quantity)
        };

        return ordered.ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void NotifyLootOrderingChanged()
    {
        OnPropertyChanged(nameof(MainLootRows));
        OnPropertyChanged(nameof(OverlayLootRows));
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Title = $"BDO Loot Tracker — {VersionText}";
        SourceInitialized += MainWindow_SourceInitialized;

        _settings = _settingsService.Load();
        // Local-only parser load. No GitHub/parser health check is performed at app startup.
        _activeParserProfile = _parserProfileService.LoadActiveProfile();
        _captureService.ConfigureParser(_activeParserProfile);
        _database = new DatabaseService(_settings.DatabasePath);
        _iconCache = new IconCacheService(_database);
        _garmothUploadService = new GarmothUploadService(_database);
        ReloadIgnoredItems(applyToCurrentSession: false);
        ReloadGarmothLootFilter();

        _captureService.LootReceived += CaptureService_LootReceived;
        _captureService.StatusChanged += CaptureService_StatusChanged;
        _captureService.CaptureError += CaptureService_CaptureError;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => RefreshMetrics();

        _autoSaveTimer = new DispatcherTimer { Interval = AutoSaveInterval };
        _autoSaveTimer.Tick += (_, _) => AutoSaveCurrentSession();

        _updateCheckTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdateAvailabilityAsync();

        Loaded += MainWindow_Loaded;
        ContentRendered += MainWindow_ContentRendered;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            UnregisterGlobalHotkeys();
            if (_hwndSource != null)
                _hwndSource.RemoveHook(WndProc);

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _updateCheckTimer.Stop();
            StopSession(saveSession: true);
            CloseOverlayWindow();
            _captureService.Dispose();
            _parserProfileService.Dispose();
            _garmothUploadService.Dispose();
            _iconCache.Dispose();
        };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshHeaderContext();
        ApplyLootPanelState(_settings.LootPanelCollapsed, persist: false);
        ApplyOverlayEnabled(_settings.OverlayEnabled, persist: false);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(CheckDatabaseAtStartup));
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
        {
            await CheckForUpdateAvailabilityAsync(showStartupPopup: true);
            _updateCheckTimer.Start();
        }));
        UpdateGarmothUploadButtonState();
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        // Changelog UI is deliberately delayed until the main window has actually
        // rendered. Velopack can restart the application very quickly after applying
        // an update; showing a modal window from Loaded proved unreliable there.
        ContentRendered -= MainWindow_ContentRendered;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowWhatsNewIfNeeded));
    }

    private async Task CheckForUpdateAvailabilityAsync(bool showStartupPopup = false)
    {
        if (_isCheckingForUpdate || _isInstallingUpdate)
            return;

        _isCheckingForUpdate = true;
        try
        {
            AppUpdateService.AvailableUpdateInfo? detectedUpdate =
                await _appUpdateService.CheckForAvailableUpdateAsync();

            if (detectedUpdate == null)
            {
                if (_availableUpdate == null)
                {
                    UpdateBannerText = string.Empty;
                    UpdateBannerVisibility = Visibility.Collapsed;
                }

                return;
            }

            _availableUpdate = detectedUpdate;
            UpdateBannerText = $"Update available • v{_availableUpdate.NewVersion} • Click here";
            UpdateBannerVisibility = Visibility.Visible;

            if (showStartupPopup && !_startupUpdatePromptShown)
            {
                // This is the one startup-only opportunity for an intrusive update
                // prompt. If the user has already started tracking while the network
                // check was in flight, keep only the bottom-right notification.
                _startupUpdatePromptShown = true;
                if (!_captureService.IsRunning)
                {
                    var window = new UpdateAvailableWindow(
                        _availableUpdate.CurrentVersion,
                        _availableUpdate.NewVersion,
                        _availableUpdate.ReleaseNotes)
                    {
                        Owner = this
                    };

                    window.ShowDialog();
                    if (window.UpdateRequested)
                        await InstallAvailableUpdateAsync(askForConfirmation: false);
                }
            }
        }
        finally
        {
            _isCheckingForUpdate = false;
        }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
        => await InstallAvailableUpdateAsync(askForConfirmation: true);

    private async Task InstallAvailableUpdateAsync(bool askForConfirmation)
    {
        if (_isInstallingUpdate)
            return;

        if (_isGarmothUploading)
        {
            AppDialog.Show(
                "Wait for the Garmoth upload to finish before updating the application.",
                "BDO Loot Tracker Update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_availableUpdate == null)
        {
            await CheckForUpdateAvailabilityAsync();
            if (_availableUpdate == null)
            {
                AppDialog.Show(
                    "No newer version is currently available.",
                    "BDO Loot Tracker Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
        }

        if (askForConfirmation)
        {
            string notes = string.IsNullOrWhiteSpace(_availableUpdate.ReleaseNotes)
                ? string.Empty
                : $"\n\nRelease notes:\n{_availableUpdate.ReleaseNotes}";

            MessageBoxResult result = AppDialog.Show(
                $"Update BDO Loot Tracker now?\n\n" +
                $"Installed: v{_availableUpdate.CurrentVersion}\n" +
                $"Available: v{_availableUpdate.NewVersion}" +
                notes +
                "\n\nThe tracker will restart automatically after the update.",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
                return;
        }

        if (_captureService.IsRunning)
            StopSession(saveSession: true);

        _isInstallingUpdate = true;
        UpdateNowButton.IsEnabled = false;
        UpdateGarmothUploadButtonState();
        UpdateBannerText = "Downloading update...";
        StatusText = "Downloading update...";

        _updateChangelogMarkerService.WritePending(_availableUpdate.NewVersion);

        var latest = _settingsService.Load();
        latest.PendingChangelogVersion = _availableUpdate.NewVersion;
        _settingsService.Save(latest);
        _settings = latest;

        try
        {
            await _appUpdateService.InstallLatestUpdateAsync();
        }
        catch (Exception ex)
        {
            _updateChangelogMarkerService.ClearPending();

            var failed = _settingsService.Load();
            failed.PendingChangelogVersion = string.Empty;
            _settingsService.Save(failed);
            _settings = failed;

            _isInstallingUpdate = false;
            UpdateNowButton.IsEnabled = true;
            UpdateGarmothUploadButtonState();
            StatusText = "Update failed";

            AppDialog.Show(
                ex.Message,
                "Update Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            await CheckForUpdateAvailabilityAsync();
        }
    }

    private void CheckDatabaseAtStartup()
    {
        try
        {
            var health = _database.GetHealth(_settings.Region, _settings.ItemLanguage);

            bool catalogMissing = !health.HasCatalog || !health.HasSelectedLanguage;
            bool marketNeedsUpdate = health.MarketIsStale(MarketMaxAge);

            if (!catalogMissing && !marketNeedsUpdate)
                return;

            string message;

            if (catalogMissing)
            {
                message =
                    "The local item database has not been downloaded yet or is incomplete.\n\n" +
                    "The tracker will still work, but item names, loot prices, and icons may be missing.\n\n" +
                    "Open Settings to fetch/update the database?";
            }
            else
            {
                int days = health.MarketUpdatedUtc == null
                    ? 999
                    : Math.Max(0, (int)(DateTime.UtcNow - health.MarketUpdatedUtc.Value).TotalDays);

                message =
                    health.MarketUpdatedUtc == null
                        ? $"The {_settings.Region} loot/price data has not been downloaded yet.\n\nOpen Settings to update it?"
                        : $"The {_settings.Region} loot/price data is approximately {days} day(s) old.\n\nAn update is recommended when the data is older than 7 days.\n\nOpen Settings now?";
            }

            var result = AppDialog.Show(
                message,
                "Database update recommended",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
                OpenSettings();
            else
                StatusText = "Database update recommended";
        }
        catch (Exception ex)
        {
            StatusText = $"Database status error: {ex.Message}";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_captureService.IsRunning)
            return;

        if (!NpcapPrerequisiteService.IsInstalled())
        {
            StatusText = "Npcap is required for packet capture";
            NpcapPrerequisiteService.PromptIfMissing();
            return;
        }

        _settings = _settingsService.Load();

        if (string.IsNullOrWhiteSpace(_settings.AdapterName))
        {
            AppDialog.Show(
                "Select a network adapter in Settings first.",
                "BDO Loot Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            OpenSettings();
            return;
        }

        // Parser self-checks are intentionally triggered only by START or the
        // explicit Diagnostics button in Settings. Application startup never
        // performs this remote check.
        StartButton.IsEnabled = false;
        StatusText = "Checking loot parser profile...";
        ParserDiagnosticsResult parserCheck = await _parserProfileService.EnsureLatestProfileAsync();
        _activeParserProfile = parserCheck.ActiveProfile;

        try
        {
            _captureService.ConfigureParser(_activeParserProfile);
        }
        catch (Exception ex)
        {
            StartButton.IsEnabled = true;
            AppDialog.Show(ex.Message, "Parser profile error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            EnsureDatabaseServicesMatchSettings();
            ReloadIgnoredItems(applyToCurrentSession: false);
            ReloadGarmothLootFilter();

            LootRows.Clear();
            NotifyLootOrderingChanged();
            _rowsById.Clear();
            _sessionLoot.Clear();
            ResetSpotDetection();

            _sessionStartedUtc = DateTime.UtcNow;
            _stoppedElapsed = TimeSpan.Zero;
            _parserProfileConfirmedThisSession = false;
            _parserRecoveryPromptShown = false;
            _nextParserHealthCheckUtc = DateTime.UtcNow.AddSeconds(30);

            CharacterClassOption? selectedClass = null;
            if (_settings.CharacterClassType != null)
            {
                selectedClass = _database.GetCharacterClasses()
                    .FirstOrDefault(x => x.ClassType == _settings.CharacterClassType.Value);
            }

            string className = selectedClass?.Name ?? string.Empty;
            string spec = string.Empty;
            if (selectedClass != null)
            {
                spec = selectedClass.Specs.FirstOrDefault(x =>
                           string.Equals(x, _settings.CharacterSpec, StringComparison.OrdinalIgnoreCase))
                       ?? selectedClass.Specs.FirstOrDefault()
                       ?? string.Empty;
            }

            ApplyClassToHeader(selectedClass, spec);
            HeaderSpotText = "Detecting grind spot...";

            _sessionId = _database.BeginSession(
                _settings.Region,
                _settings.CharacterName,
                selectedClass?.ClassType,
                className,
                spec);

            _captureService.Start(_settings.AdapterName);
            _timer.Start();
            _autoSaveTimer.Start();

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            UpdateGarmothUploadButtonState();
            string characterStatus = selectedClass == null
                ? string.Empty
                : $" • {className}{(string.IsNullOrWhiteSpace(spec) ? string.Empty : $" {spec}")}";
            StatusText = $"Tracking active • {_settings.Region}{characterStatus}";
            RefreshMetrics();
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Capture error", MessageBoxButton.OK, MessageBoxImage.Error);
            StopSession(saveSession: false);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
        => StopSession(saveSession: true);

    private void StopSession(bool saveSession)
    {
        if (_captureService.IsRunning)
            _captureService.Stop();

        _timer.Stop();
        _autoSaveTimer.Stop();

        if (_sessionId > 0)
        {
            try
            {
                if (saveSession)
                {
                    _database.EndSession(_sessionId, CreateSessionSnapshot());
                        }
                else
                {
                    // Ha a capture már a START közben elhasal, ne maradjon üres/fél session a historyban.
                    _database.DeleteSession(_sessionId);
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Session save error: {ex.Message}";
            }
        }

        if (_sessionStartedUtc != null)
            _stoppedElapsed = DateTime.UtcNow - _sessionStartedUtc.Value;

        if (saveSession && _sessionId > 0)
            StatusText = $"Session saved • {_settings.Region}";

        _sessionId = 0;
        _sessionStartedUtc = null;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        UpdateGarmothUploadButtonState();
        RefreshMetrics();
    }

    private void AutoSaveCurrentSession()
    {
        if (_sessionId <= 0)
            return;

        try
        {
            // 10 másodpercenként akkor is frissítjük a LastSavedAtUtc mezőt,
            // ha épp nem esett új loot. Így crash után az időtartam is közel pontos marad.
            _database.SaveSessionProgress(_sessionId, CreateSessionSnapshot());
        }
        catch (Exception ex)
        {
            StatusText = $"Session autosave error: {ex.Message}";
        }
    }

    private List<SessionLootSnapshot> CreateSessionSnapshot()
        => LootRows.Select(row => new SessionLootSnapshot
        {
            ItemId = row.ItemId,
            Quantity = row.Quantity,
            Name = row.Name,
            UnitPrice = row.UnitPrice,
            IsTrash = row.IsTrash,
            IconPath = row.IconPath
        }).ToList();

    private void CaptureService_LootReceived(uint itemId, ulong quantity)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_parserProfileConfirmedThisSession)
            {
                _parserProfileConfirmedThisSession = true;
                _parserProfileService.MarkProfileAsLastKnownGood(_activeParserProfile);
            }

            AddLoot(itemId, quantity);
        });
    }

    private void AddLoot(uint itemId, ulong quantity)
    {
        if (_sessionStartedUtc == null)
            return;

        // User-managed ignore list: ezek az ID-k már a sessionbe sem kerülnek be.
        if (_ignoredItemIds.Contains(itemId))
            return;

        // Optional Garmoth-only filter. If the local drop cache is unavailable,
        // fail open rather than silently losing an entire session.
        if (_settings.OnlyTrackGarmothItems &&
            _garmothKnownItemIds.Count > 0 &&
            !_garmothKnownItemIds.Contains(itemId))
        {
            return;
        }

        if (!_sessionLoot.TryAdd(itemId, quantity))
            _sessionLoot[itemId] += quantity;

        if (!_rowsById.TryGetValue(itemId, out var row))
        {
            var item = _database.GetItem(itemId, _settings.Region, _settings.ItemLanguage);

            row = new LootRowViewModel
            {
                ItemId = item.ItemId,
                Name = item.Name,
                IconPath = item.LocalIconPath,
                IsTrash = item.IsTrash,
                UnitPrice = item.ItemId == 1 && item.UnitPrice == 0 ? 1 : item.UnitPrice,
                Quantity = 0
            };

            _rowsById[itemId] = row;
            LootRows.Add(row);
            NotifyLootOrderingChanged();

            if (string.IsNullOrWhiteSpace(row.IconPath) || !File.Exists(row.IconPath))
                _ = EnsureRowIconAsync(row);
        }

        row.Quantity += quantity;
        row.LastLootedUtc = DateTime.UtcNow;
        NotifyLootOrderingChanged();
        EvaluateSpotDetection(itemId, row.IsTrash);
        RefreshMetrics();
    }

    private async Task EnsureRowIconAsync(LootRowViewModel row)
    {
        try
        {
            string? path = await _iconCache.EnsureIconAsync(row.ItemId);
            if (string.IsNullOrWhiteSpace(path))
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                row.IconPath = path;
                    });
        }
        catch
        {
            // Az icon nem kritikus: a tracker tovább működik nélküle.
        }
    }

    private void RefreshMetrics()
    {
        TimeSpan elapsed = _sessionStartedUtc == null
            ? _stoppedElapsed
            : DateTime.UtcNow - _sessionStartedUtc.Value;

        SessionTimeText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        decimal totalSilver = LootRows.Sum(x => x.TotalSilver);
        ulong totalTrash = 0;

        foreach (var row in LootRows)
        {
            if (row.IsTrash)
                totalTrash += row.Quantity;
        }

        TotalSilverText = $"{totalSilver:N0}";

        if (elapsed.TotalHours > 0.0001)
        {
            decimal silverPerHour = totalSilver / (decimal)elapsed.TotalHours;
            decimal trashPerHour = totalTrash / (decimal)elapsed.TotalHours;

            SilverPerHourText = $"{silverPerHour:N0}";
            TrashPerHourText = totalTrash == 0 ? "—" : $"{trashPerHour:N0}";
        }
        else
        {
            SilverPerHourText = "0";
            TrashPerHourText = "—";
        }

        CheckParserHealthDuringSession();
    }

    private void CheckParserHealthDuringSession()
    {
        if (_sessionStartedUtc == null || !_captureService.IsRunning || _parserRecoveryPromptShown)
            return;

        DateTime now = DateTime.UtcNow;
        if (now < _nextParserHealthCheckUtc)
            return;

        _nextParserHealthCheckUtc = now.AddSeconds(30);

        // Conservative heuristic: only warn after a session has been running for
        // several minutes, substantial BDO server traffic is present, and the
        // parser has not produced a single valid loot event.
        TimeSpan elapsed = now - _sessionStartedUtc.Value;
        if (elapsed < TimeSpan.FromMinutes(6) ||
            _captureService.ServerPayloadBytesReceived < 5_000_000 ||
            _captureService.ValidLootCount > 0)
        {
            return;
        }

        _parserRecoveryPromptShown = true;
        _ = ShowParserRecoveryPromptAsync();
    }

    private async Task ShowParserRecoveryPromptAsync()
    {
        var window = new ParserRecoveryWindow(
            _activeParserProfile.ProfileVersion,
            "BDO server traffic is active, but this session has not produced any valid loot events. If you are actively grinding, the latest game patch may have changed the loot packet format.")
        {
            Owner = this
        };

        window.ShowDialog();

        if (window.SelectedAction == ParserRecoveryAction.Diagnostics)
        {
            OpenSettings();
            return;
        }

        if (window.SelectedAction != ParserRecoveryAction.AutoRepair)
            return;

        StatusText = "Running parser Auto Repair...";
        bool captureWasRunning = _captureService.IsRunning;
        if (captureWasRunning)
            _captureService.Stop();

        ParserDiagnosticsResult result = await _parserProfileService.AutoRepairAsync();
        _activeParserProfile = result.ActiveProfile;

        try
        {
            _captureService.ConfigureParser(_activeParserProfile);
            if (captureWasRunning && _sessionStartedUtc != null)
                _captureService.Start(_settings.AdapterName);

            StatusText = result.Success
                ? $"Parser repaired • {_activeParserProfile.ProfileVersion}"
                : result.Message;

            AppDialog.Show(
                result.Message,
                result.Success ? "Parser Auto Repair" : "Parser Recovery",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText = $"Parser recovery failed: {ex.Message}";
            AppDialog.Show(ex.Message, "Parser Recovery", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CaptureService_StatusChanged(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ConnectionText = status;
            StatusBrush = status.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) ? Brushes.LimeGreen : Brushes.Gray;
        });
    }

    private void CaptureService_CaptureError(Exception ex)
    {
        Dispatcher.BeginInvoke(() => StatusText = $"Capture error: {ex.Message}");
    }

    private void UpdateGarmothUploadButtonState()
    {
        if (_isGarmothUploading || _isInstallingUpdate)
        {
            MainGarmothUploadButton.IsEnabled = false;
            MainGarmothUploadButton.Content = _isGarmothUploading ? "Uploading..." : "Update in progress...";
            return;
        }

        MainGarmothUploadButton.Content = "☁  REVIEW / UPLOAD SESSION";

        bool hasCompletedSession = false;
        if (!_captureService.IsRunning)
        {
            try
            {
                hasCompletedSession = _database
                    .GetSessions(_settings.ItemLanguage, hideIgnored: true, limit: 20)
                    .Any(x => x.IsCompleted);
            }
            catch
            {
                hasCompletedSession = false;
            }
        }

        MainGarmothUploadButton.IsEnabled = !_captureService.IsRunning && hasCompletedSession;
    }

    private void UploadLastSessionToGarmoth_Click(object sender, RoutedEventArgs e)
    {
        if (_isGarmothUploading)
            return;

        if (_captureService.IsRunning)
        {
            AppDialog.Show(
                "Stop the active session before uploading to Garmoth.",
                "Upload to Garmoth",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _settings = _settingsService.Load();
        EnsureDatabaseServicesMatchSettings();

        if (string.IsNullOrWhiteSpace(_settings.GarmothApiKey))
        {
            AppDialog.Show(
                "No Garmoth API token is saved. Open Settings → Garmoth, paste your API token, and press Save Changes.",
                "Upload to Garmoth",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            SessionSummary? session = _database
                .GetSessions(_settings.ItemLanguage, hideIgnored: true, limit: 50)
                .FirstOrDefault(x => x.IsCompleted);

            if (session == null)
            {
                AppDialog.Show(
                    "There is no completed session to upload yet.",
                    "Upload to Garmoth",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                UpdateGarmothUploadButtonState();
                return;
            }

            IReadOnlyList<SessionLootHistoryRow> uploadLoot =
                _database.GetSessionLoot(session.SessionId, _settings.ItemLanguage, hideIgnored: true);

            if (uploadLoot.Count == 0)
            {
                AppDialog.Show(
                    "The most recent completed session has no uploadable loot items.",
                    "Upload to Garmoth",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var preview = new GarmothUploadPreviewWindow(
                _database,
                _garmothUploadService,
                _settings.GarmothApiKey,
                session,
                uploadLoot)
            {
                Owner = this
            };

            preview.ShowDialog();
            if (preview.UploadedSuccessfully)
                StatusText = "Session uploaded to Garmoth successfully";
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Upload to Garmoth", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateGarmothUploadButtonState();
        }
    }

    private void Sessions_Click(object sender, RoutedEventArgs e)
    {
        // Ha fut session, előbb írjuk ki a legfrissebb snapshotot, hogy a history ablakban is látszódjon.
        AutoSaveCurrentSession();

        var window = new SessionHistoryWindow(_database, _settingsService, _settings.ItemLanguage, _sessionId)
        {
            Owner = this
        };

        window.ShowDialog();

        // A history ablakból új Ignore tétel kerülhetett az adatbázisba.
        ReloadIgnoredItems(applyToCurrentSession: true);
        UpdateGarmothUploadButtonState();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
        => OpenSettings();

    private void OpenSettings()
    {
        if (_captureService.IsRunning)
        {
            AppDialog.Show(
                "Stop the active session before changing network or database settings.",
                "BDO Loot Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Global hotkeys can otherwise consume the same keys the user is trying
        // to enter into the Keybind fields inside the modal Settings window.
        UnregisterGlobalHotkeys();

        try
        {
            var window = new SettingsWindow(_settingsService)
            {
                Owner = this
            };
            window.MoveOverlayRequested += (_, _) => BeginOverlayPlacement(window);
            window.OverlayResetRequested += (_, _) =>
            {
                _settings = _settingsService.Load();
                NotifyLootOrderingChanged();
                RefreshOverlayWindowFromSettings();
            };

            if (window.ShowDialog() == true)
            {
                _settings = _settingsService.Load();
                EnsureDatabaseServicesMatchSettings(forceRecreateIconCache: true);
                ReloadIgnoredItems(applyToCurrentSession: true);
                ReloadGarmothLootFilter();
                RefreshVisibleLootDefinitions();
                RefreshHeaderContext();
                NotifyLootOrderingChanged();
                RefreshOverlayWindowFromSettings();
                UpdateGarmothUploadButtonState();
                StatusText = $"Settings saved • {_settings.Region}";
            }
            else
            {
                // Overlay placement/reset is saved independently from the Settings
                // Save button. Restore the normal overlay even when the rest of
                // Settings is cancelled.
                _settings = _settingsService.Load();
                NotifyLootOrderingChanged();
                RefreshOverlayWindowFromSettings();
                UpdateGarmothUploadButtonState();
            }
        }
        finally
        {
            ApplyGlobalHotkeys(showErrors: true);
        }
    }

    private void EnsureDatabaseServicesMatchSettings(bool forceRecreateIconCache = false)
    {
        bool databaseChanged = !string.Equals(
            _database.DatabasePath,
            _settings.DatabasePath,
            StringComparison.OrdinalIgnoreCase);

        if (databaseChanged)
            _database.ChangeDatabase(_settings.DatabasePath);

        if (databaseChanged || forceRecreateIconCache)
        {
            _iconCache.Dispose();
            _iconCache = new IconCacheService(_database);
        }
    }

    private void ReloadGarmothLootFilter()
    {
        try
        {
            _garmothKnownItemIds = _database.GetGarmothKnownLootItemIds();
        }
        catch
        {
            _garmothKnownItemIds = new HashSet<uint>();
        }
    }

    private void ReloadIgnoredItems(bool applyToCurrentSession)
    {
        try
        {
            _ignoredItemIds = _database.GetIgnoredItemIds();

            if (!applyToCurrentSession || _ignoredItemIds.Count == 0)
                return;

            var rowsToRemove = LootRows
                .Where(x => _ignoredItemIds.Contains(x.ItemId))
                .ToList();

            foreach (var row in rowsToRemove)
            {
                LootRows.Remove(row);
                _rowsById.Remove(row.ItemId);
                _sessionLoot.Remove(row.ItemId);
                    }

            if (rowsToRemove.Count > 0)
            {
                NotifyLootOrderingChanged();
                RefreshMetrics();
                AutoSaveCurrentSession();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Ignore list error: {ex.Message}";
        }
    }

    private void RefreshVisibleLootDefinitions()
    {
        foreach (var row in LootRows)
        {
            var item = _database.GetItem(row.ItemId, _settings.Region, _settings.ItemLanguage);
            row.ApplyDefinition(item);

            if (string.IsNullOrWhiteSpace(row.IconPath) || !File.Exists(row.IconPath))
                _ = EnsureRowIconAsync(row);
        }

        RefreshMetrics();
    }

    private void RefreshHeaderContext()
    {
        CharacterClassOption? selectedClass = null;
        if (_settings.CharacterClassType != null)
        {
            selectedClass = _database.GetCharacterClasses()
                .FirstOrDefault(x => x.ClassType == _settings.CharacterClassType.Value);
        }

        string spec = string.Empty;
        if (selectedClass != null)
        {
            spec = selectedClass.Specs.FirstOrDefault(x =>
                       string.Equals(x, _settings.CharacterSpec, StringComparison.OrdinalIgnoreCase))
                   ?? selectedClass.Specs.FirstOrDefault()
                   ?? string.Empty;
        }

        ApplyClassToHeader(selectedClass, spec);

        if (_sessionStartedUtc == null && string.IsNullOrWhiteSpace(_detectedSpotName))
            HeaderSpotText = "Spot: —";
    }

    private void ApplyClassToHeader(CharacterClassOption? selectedClass, string spec)
    {
        if (selectedClass == null)
        {
            HeaderClassIconPath = null;
            HeaderClassText = "Class not set";
            return;
        }

        HeaderClassIconPath = !string.IsNullOrWhiteSpace(selectedClass.IconPath) && File.Exists(selectedClass.IconPath)
            ? selectedClass.IconPath
            : null;

        HeaderClassText = string.IsNullOrWhiteSpace(spec)
            ? selectedClass.Name
            : $"{selectedClass.Name} • {spec}";
    }

    private void ResetSpotDetection()
    {
        _spotScores.Clear();
        _spotNames.Clear();
        _spotScoredItemIds.Clear();
        _detectedSpotKey = string.Empty;
        _detectedSpotName = string.Empty;
        HeaderSpotText = "Detecting grind spot...";
    }

    private void EvaluateSpotDetection(uint itemId, bool isTrash)
    {
        if (_sessionStartedUtc == null || !_spotScoredItemIds.Add(itemId))
            return;

        IReadOnlyList<SpotCandidate> candidates;
        try
        {
            candidates = _database.GetSpotCandidatesForItem(itemId);
        }
        catch
        {
            return;
        }

        if (candidates.Count == 0)
        {
            // Some brand-new grind zones appear in the Garmoth drops database
            // before the spot relationship feed catches up. Only use a built-in
            // fallback for known UNIQUE trash items; never infer a spot from a
            // common item such as Black Stone or Caphras Stone.
            if (isTrash && KnownTrashSpotFallbacks.TryGetValue(itemId, out var fallback))
                ApplyDetectedSpot(fallback.Key, fallback.Name);

            return;
        }

        // Trash items are strong identifiers. Shared/common drops only contribute
        // a small amount so Black Stone / Caphras alone cannot choose a spot.
        double weight = isTrash
            ? (candidates.Count == 1 ? 120.0 : 55.0)
            : (candidates.Count == 1 ? 12.0 : 2.0);

        foreach (var candidate in candidates)
        {
            _spotNames[candidate.SpotKey] = candidate.Name;
            if (!_spotScores.TryAdd(candidate.SpotKey, weight))
                _spotScores[candidate.SpotKey] += weight;
        }

        var ordered = _spotScores
            .OrderByDescending(x => x.Value)
            .ThenBy(x => _spotNames.TryGetValue(x.Key, out string? name) ? name : x.Key)
            .ToList();

        if (ordered.Count == 0)
            return;

        var best = ordered[0];
        double secondScore = ordered.Count > 1 ? ordered[1].Value : 0;

        bool confident = best.Value >= 50 ||
                         (best.Value >= 24 && best.Value - secondScore >= 12);

        if (!confident)
            return;

        string bestName = _spotNames.TryGetValue(best.Key, out string? detectedName)
            ? detectedName
            : best.Key;

        ApplyDetectedSpot(best.Key, bestName);
    }

    private void ApplyDetectedSpot(string spotKey, string spotName)
    {
        if (string.IsNullOrWhiteSpace(spotKey) || string.IsNullOrWhiteSpace(spotName))
            return;

        if (string.Equals(_detectedSpotKey, spotKey, StringComparison.OrdinalIgnoreCase))
            return;

        _detectedSpotKey = spotKey;
        _detectedSpotName = spotName;
        HeaderSpotText = spotName;

        if (_sessionId > 0)
        {
            try
            {
                _database.UpdateSessionSpot(_sessionId, _detectedSpotKey, _detectedSpotName);
            }
            catch (Exception ex)
            {
                StatusText = $"Spot save error: {ex.Message}";
            }
        }
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        ApplyOverlayEnabled(!_settings.OverlayEnabled, persist: true);
    }

    private void ApplyOverlayEnabled(bool enabled, bool persist)
    {
        _settings.OverlayEnabled = enabled;

        if (enabled)
            EnsureOverlayWindowVisible();
        else
            CloseOverlayWindow();

        UpdateOverlayButtonVisual();

        if (persist)
            _settingsService.Save(_settings);
    }

    private void UpdateOverlayButtonVisual()
    {
        if (_settings.OverlayEnabled)
        {
            OverlayButtonBackground = new SolidColorBrush(Color.FromRgb(22, 122, 73));
            OverlayButtonBorderBrush = new SolidColorBrush(Color.FromRgb(46, 201, 126));
        }
        else
        {
            OverlayButtonBackground = new SolidColorBrush(Color.FromRgb(20, 32, 42));
            OverlayButtonBorderBrush = new SolidColorBrush(Color.FromRgb(42, 62, 77));
        }
    }

    private void EnsureOverlayWindowVisible()
    {
        if (_overlayWindow != null)
        {
            if (!_overlayWindow.IsVisible)
                _overlayWindow.Show();
            return;
        }

        _overlayWindow = new OverlayWindow(this, _settingsService, _settings);
        _overlayWindow.Closed += (_, _) => _overlayWindow = null;
        _overlayWindow.Show();
    }

    private void CloseOverlayWindow()
    {
        if (_overlayWindow == null)
            return;

        var window = _overlayWindow;
        _overlayWindow = null;
        window.Close();
    }

    private void RefreshOverlayWindowFromSettings()
    {
        UpdateOverlayButtonVisual();

        if (!_settings.OverlayEnabled)
        {
            CloseOverlayWindow();
            return;
        }

        // Recreate the window so mode, opacity, max item count and saved size are
        // applied together without leaving any stale WPF layout state behind.
        CloseOverlayWindow();
        EnsureOverlayWindowVisible();
    }

    private void BeginOverlayPlacement(SettingsWindow settingsWindow)
    {
        CloseOverlayWindow();

        var previewSettings = settingsWindow.GetOverlayPreviewSettings();
        NotifyLootOrderingChanged();

        var editor = new OverlayWindow(this, _settingsService, previewSettings, editMode: true);
        _overlayWindow = editor;

        void RestoreSettingsWindow()
        {
            // SettingsWindow was opened with ShowDialog(). Do not Hide()/Show() it,
            // otherwise WPF loses the modal-dialog state and DialogResult can no
            // longer be assigned from Save/Cancel. Restore the minimized dialog
            // instead.
            if (settingsWindow.WindowState == WindowState.Minimized)
                settingsWindow.WindowState = WindowState.Normal;

            settingsWindow.Activate();
            settingsWindow.Focus();
        }

        editor.PlacementFinished += _ =>
        {
            _settings = _settingsService.Load();
            RestoreSettingsWindow();
        };

        editor.Closed += (_, _) =>
        {
            if (ReferenceEquals(_overlayWindow, editor))
                _overlayWindow = null;

            // Also covers Alt+F4 while placement mode is active.
            RestoreSettingsWindow();
        };

        editor.Show();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        HideNativeTitleBarIcon();

        _mainHwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_mainHwnd);
        _hwndSource?.AddHook(WndProc);
        ApplyGlobalHotkeys(showErrors: false);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotKey)
            return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (id == StartStopHotkeyId)
        {
            if (_captureService.IsRunning)
                StopSession(saveSession: true);
            else
                Start_Click(StartButton, new RoutedEventArgs());

            handled = true;
        }
        else if (id == OverlayHotkeyId)
        {
            ApplyOverlayEnabled(!_settings.OverlayEnabled, persist: true);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ApplyGlobalHotkeys(bool showErrors)
    {
        if (_mainHwnd == IntPtr.Zero)
            return;

        UnregisterGlobalHotkeys();
        _settings = _settingsService.Load();

        var errors = new List<string>();
        RegisterConfiguredHotkey(_settings.StartStopHotkey, StartStopHotkeyId, "Start / Stop Tracking", errors);
        RegisterConfiguredHotkey(_settings.OverlayHotkey, OverlayHotkeyId, "Toggle Overlay", errors);

        if (errors.Count == 0)
            return;

        StatusText = errors[0];
        if (showErrors)
        {
            AppDialog.Show(
                string.Join("\n", errors),
                "Keybinds",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void RegisterConfiguredHotkey(string? hotkey, int id, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(hotkey) || string.Equals(hotkey.Trim(), "None", StringComparison.OrdinalIgnoreCase))
            return;

        if (!TryParseHotkey(hotkey, out uint modifiers, out uint virtualKey))
        {
            errors.Add($"{label}: invalid shortcut '{hotkey}'.");
            return;
        }

        if (!RegisterHotKey(_mainHwnd, id, modifiers | ModNoRepeat, virtualKey))
            errors.Add($"{label}: shortcut '{hotkey}' is already in use.");
    }

    private void UnregisterGlobalHotkeys()
    {
        if (_mainHwnd == IntPtr.Zero)
            return;

        UnregisterHotKey(_mainHwnd, StartStopHotkeyId);
        UnregisterHotKey(_mainHwnd, OverlayHotkeyId);
    }

    private static bool TryParseHotkey(string value, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        string[] parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        string keyText = parts[^1];
        foreach (string part in parts[..^1])
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        try
        {
            var converter = new KeyConverter();
            object? converted = converter.ConvertFromString(keyText);
            if (converted is not Key key || key == Key.None)
                return false;

            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            return virtualKey != 0;
        }
        catch
        {
            return false;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        if (_closePromptOpen)
            return;

        _closePromptOpen = true;
        try
        {
            var dialog = new CloseChoiceWindow { Owner = this };
            dialog.ShowDialog();

            switch (dialog.Choice)
            {
                case CloseChoice.Exit:
                    _allowClose = true;
                    Dispatcher.BeginInvoke(new Action(Close));
                    break;
                case CloseChoice.Tray:
                    HideToTray();
                    break;
                case CloseChoice.Cancel:
                default:
                    break;
            }
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null)
            return;

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "BDO Loot Tracker",
            Visible = false
        };

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
                _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch
        {
            // The tray icon can still exist without a custom icon if Windows
            // cannot extract the executable icon for some reason.
        }

        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open BDO Loot Tracker");
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExitFromTray));
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
    }

    private void HideToTray()
    {
        EnsureTrayIcon();
        if (_trayIcon != null)
            _trayIcon.Visible = true;

        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();

        if (_trayIcon != null)
            _trayIcon.Visible = false;
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        Close();
    }

    private void ShowWhatsNewIfNeeded()
    {
        try
        {
            string currentVersion = VersionText.TrimStart('v', 'V');
            _settings = _settingsService.Load();

            string markerVersion = _updateChangelogMarkerService.ReadPendingVersion();
            bool forcedByFileMarker = string.Equals(
                markerVersion,
                currentVersion,
                StringComparison.OrdinalIgnoreCase);

            bool forcedBySettings = string.Equals(
                _settings.PendingChangelogVersion,
                currentVersion,
                StringComparison.OrdinalIgnoreCase);

            bool forcedByUpdater = forcedByFileMarker || forcedBySettings;

            if (!forcedByUpdater &&
                string.Equals(_settings.LastSeenChangelogVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // An update confirmation should never disappear just because a release
            // accidentally shipped without a matching changelog.json entry.
            var entry = new ChangelogService().GetForVersion(currentVersion)
                        ?? CreateFallbackChangelog(currentVersion, forcedByUpdater);

            var window = new WhatsNewWindow(currentVersion, entry)
            {
                Owner = this
            };

            window.ShowDialog();

            // Only acknowledge the version after the dialog was successfully shown.
            // If window creation/display throws, both pending markers remain and the
            // next application start retries instead of silently losing the changelog.
            var latest = _settingsService.Load();
            latest.LastSeenChangelogVersion = currentVersion;
            if (forcedBySettings)
                latest.PendingChangelogVersion = string.Empty;
            _settingsService.Save(latest);
            _settings = latest;

            if (forcedByFileMarker)
                _updateChangelogMarkerService.ClearPending();
        }
        catch (Exception ex)
        {
            // Never block startup, but also do not consume the pending markers.
            // This leaves the changelog eligible for another attempt next launch.
            Debug.WriteLine($"Unable to show What's New: {ex}");
            StatusText = "What's New could not open; it will retry next start.";
        }
    }

    private static ChangelogEntry CreateFallbackChangelog(string currentVersion, bool installedByUpdater)
    {
        string description = installedByUpdater
            ? "The update was installed successfully. Detailed changelog information is not available for this build."
            : "This version is running successfully. Detailed changelog information is not available for this build.";

        return new ChangelogEntry
        {
            Title = "What's New",
            Changes = new List<ChangelogChange>
            {
                new()
                {
                    Title = $"BDO Loot Tracker v{currentVersion}",
                    Description = description
                }
            }
        };
    }

    private void ToggleLootPanel_Click(object sender, RoutedEventArgs e)
    {
        ApplyLootPanelState(!_lootPanelCollapsed, persist: true);
    }

    private void ApplyLootPanelState(bool collapsed, bool persist)
    {
        _lootPanelCollapsed = collapsed;

        if (collapsed)
        {
            if (ActualWidth >= ExpandedMinWidth)
                _expandedWindowWidth = ActualWidth;

            LootPanelVisibility = Visibility.Collapsed;
            LootSeparatorWidth = new GridLength(0);
            LootColumnWidth = new GridLength(0);
            ExpandedHeaderVisibility = Visibility.Collapsed;
            CompactHeaderVisibility = Visibility.Visible;
            MinWidth = CollapsedMinWidth;
            Width = CollapsedWindowWidth;
            LootToggleText = "▶";
        }
        else
        {
            LootPanelVisibility = Visibility.Visible;
            LootSeparatorWidth = new GridLength(12);
            LootColumnWidth = new GridLength(1, GridUnitType.Star);
            ExpandedHeaderVisibility = Visibility.Visible;
            CompactHeaderVisibility = Visibility.Collapsed;
            MinWidth = ExpandedMinWidth;
            Width = Math.Max(_expandedWindowWidth, ExpandedMinWidth);
            LootToggleText = "◀";
        }

        if (!persist)
            return;

        _settings.LootPanelCollapsed = collapsed;
        _settingsService.Save(_settings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
