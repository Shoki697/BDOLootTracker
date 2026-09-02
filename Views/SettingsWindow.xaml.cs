using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BDOLootTracker.Models;
using BDOLootTracker.Services;
using Microsoft.Win32;
using SharpPcap;

namespace BDOLootTracker.Views;

public partial class SettingsWindow : Window
{
    private static readonly TimeSpan MarketMaxAge = TimeSpan.FromDays(7);

    private readonly SettingsService _settingsService;
    private readonly DatabaseFetchService _fetchService = new();
    private AppSettings _settings;
    private ClassIconService? _classIconService;
    private bool _isLoading = true;
    private bool _isFetching;

    public event EventHandler? MoveOverlayRequested;
    public event EventHandler? OverlayResetRequested;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _settings = _settingsService.Load();

        LoadAdapters();
        LoadValues();

        _isLoading = false;
        RefreshDatabaseStatus();
        RefreshIgnoreList();
        SetActiveNavButton(NetworkNavButton);

        Closing += SettingsWindow_Closing;
        Closed += (_, _) =>
        {
            _classIconService?.Dispose();
            _fetchService.Dispose();
        };
    }


    private void NavigateSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string sectionName)
            return;

        FrameworkElement? section = sectionName switch
        {
            nameof(NetworkSection) => NetworkSection,
            nameof(DatabaseSection) => DatabaseSection,
            nameof(CharacterSection) => CharacterSection,
            nameof(GarmothSection) => GarmothSection,
            nameof(OverlaySection) => OverlaySection,
            nameof(KeybindsSection) => KeybindsSection,
            nameof(IgnoreSection) => IgnoreSection,
            _ => null
        };

        if (section == null)
            return;

        // Translate the section into the scrolling content's coordinate system.
        // This gives deterministic jumps even when panel heights change after a
        // database refresh or when the window is resized.
        Point point = section.TranslatePoint(new Point(0, 0), SettingsContentPanel);
        SettingsScrollViewer.ScrollToVerticalOffset(Math.Max(0, point.Y));
        SetActiveNavButton(button);
    }

    private void SetActiveNavButton(Button activeButton)
    {
        Button[] buttons =
        {
            NetworkNavButton,
            DatabaseNavButton,
            CharacterNavButton,
            GarmothNavButton,
            OverlayNavButton,
            KeybindsNavButton,
            IgnoreNavButton
        };

        foreach (Button button in buttons)
        {
            button.Background = ReferenceEquals(button, activeButton)
                ? (System.Windows.Media.Brush)FindResource("PanelHover")
                : System.Windows.Media.Brushes.Transparent;

            button.BorderBrush = ReferenceEquals(button, activeButton)
                ? (System.Windows.Media.Brush)FindResource("Accent")
                : System.Windows.Media.Brushes.Transparent;
        }
    }

    private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isFetching)
            return;

        e.Cancel = true;
        MessageBox.Show(
            "Please wait for the database update to finish.",
            "Database update",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void LoadAdapters()
    {
        var adapters = CaptureDeviceList.Instance
            .Select(d => new NetworkAdapterOption
            {
                Name = d.Name,
                Description = string.IsNullOrWhiteSpace(d.Description) ? d.Name : d.Description
            })
            .ToList();

        AdapterCombo.ItemsSource = adapters;

        var selected = adapters.FirstOrDefault(x =>
            string.Equals(x.Name, _settings.AdapterName, StringComparison.OrdinalIgnoreCase));

        AdapterCombo.SelectedItem = selected;
    }

    private void LoadValues()
    {
        RegionCombo.ItemsSource = new[] { "EU", "NA" };
        RegionCombo.SelectedItem = DatabaseService.NormalizeRegion(_settings.Region);
        if (RegionCombo.SelectedIndex < 0)
            RegionCombo.SelectedIndex = 0;

        LanguageCombo.ItemsSource = LanguageOption.All;
        LanguageCombo.SelectedValue = DatabaseService.NormalizeLanguage(_settings.ItemLanguage);
        if (LanguageCombo.SelectedIndex < 0)
            LanguageCombo.SelectedValue = "us";

        DatabasePathBox.Text = _settings.DatabasePath;
        CharacterBox.Text = _settings.CharacterName;
        GarmothApiKeyBox.Password = _settings.GarmothApiKey ?? string.Empty;

        OverlayModeCombo.ItemsSource = new[] { "Detailed", "Compact" };
        OverlayModeCombo.SelectedItem = string.Equals(_settings.OverlayMode, "Compact", StringComparison.OrdinalIgnoreCase)
            ? "Compact"
            : "Detailed";
        OverlayOpacitySlider.Value = Math.Clamp(_settings.OverlayBackgroundOpacity, 0.10, 1.0) * 100.0;
        OverlayOpacityValueText.Text = $"{OverlayOpacitySlider.Value:0}%";
        OverlayMaxItemsBox.Text = Math.Clamp(_settings.OverlayMaxDisplayedItems, 1, 20).ToString();

        OverlaySortByCombo.ItemsSource = new[] { "Quantity", "Last Looted", "Total Value", "Unit Price" };
        OverlaySortByCombo.SelectedItem = NormalizeOverlaySortBy(_settings.OverlaySortBy);
        OverlaySortOrderCombo.ItemsSource = new[] { "Descending", "Ascending" };
        OverlaySortOrderCombo.SelectedItem = _settings.OverlaySortDescending ? "Descending" : "Ascending";

        StartStopHotkeyBox.Text = NormalizeHotkeyText(_settings.StartStopHotkey);
        OverlayHotkeyBox.Text = NormalizeHotkeyText(_settings.OverlayHotkey);

        ReloadCharacterClassesFromCurrentPath(
            preferredClassType: _settings.CharacterClassType,
            preferredSpec: _settings.CharacterSpec);
    }

    private void ReloadCharacterClassesFromCurrentPath(
        int? preferredClassType = null,
        string? preferredSpec = null)
    {
        string path = DatabasePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;

        int? classToRestore = preferredClassType;
        string? specToRestore = preferredSpec;

        if (classToRestore == null && ClassCombo.SelectedItem is CharacterClassOption current && current.ClassType >= 0)
            classToRestore = current.ClassType;

        if (string.IsNullOrWhiteSpace(specToRestore))
            specToRestore = SpecCombo.SelectedItem?.ToString();

        bool previousLoading = _isLoading;
        _isLoading = true;

        try
        {
            var database = new DatabaseService(path);
            var classes = new List<CharacterClassOption> { CharacterClassOption.None };
            classes.AddRange(database.GetCharacterClasses());

            ClassCombo.ItemsSource = classes;

            var selected = classToRestore == null
                ? CharacterClassOption.None
                : classes.FirstOrDefault(x => x.ClassType == classToRestore.Value) ?? CharacterClassOption.None;

            ClassCombo.SelectedItem = selected;
            RefreshSpecOptions(specToRestore);

            _classIconService?.Dispose();
            _classIconService = new ClassIconService(database);
            _ = EnsureClassIconsAsync(classes.Where(x => x.ClassType >= 0).ToArray(), _classIconService);
        }
        catch
        {
            ClassCombo.ItemsSource = new[] { CharacterClassOption.None };
            ClassCombo.SelectedItem = CharacterClassOption.None;
            RefreshSpecOptions(null);
        }
        finally
        {
            _isLoading = previousLoading;
        }
    }

    private async Task EnsureClassIconsAsync(
        IReadOnlyList<CharacterClassOption> classes,
        ClassIconService service)
    {
        // Small first-run cache. Limit parallelism so opening Settings does not burst the CDN.
        using var gate = new SemaphoreSlim(4);

        var tasks = classes.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                await service.EnsureIconAsync(item);
            }
            catch
            {
                // Cosmetic only. Initials remain visible if an icon cannot be downloaded.
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private void RefreshSpecOptions(string? preferredSpec)
    {
        if (ClassCombo.SelectedItem is not CharacterClassOption selected || selected.ClassType < 0)
        {
            SpecCombo.ItemsSource = null;
            SpecCombo.SelectedItem = null;
            SpecCombo.IsEnabled = false;
            return;
        }

        SpecCombo.ItemsSource = selected.Specs;
        SpecCombo.IsEnabled = selected.Specs.Count > 0 && !_isFetching;

        var wanted = selected.Specs.FirstOrDefault(x =>
            string.Equals(x, preferredSpec, StringComparison.OrdinalIgnoreCase));

        SpecCombo.SelectedItem = wanted ?? selected.Specs.FirstOrDefault();
    }

    private void ClassCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
            return;

        string? previousSpec = SpecCombo.SelectedItem?.ToString();
        RefreshSpecOptions(previousSpec);
    }

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e)
        => LoadAdapters();

    private void DatabaseOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && !_isFetching)
            RefreshDatabaseStatus();
    }

    private void DatabasePathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isFetching)
            return;

        RefreshDatabaseStatus();
        RefreshIgnoreList();
        ReloadCharacterClassesFromCurrentPath();
    }

    private void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "SQLite database",
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".db",
            FileName = Path.GetFileName(DatabasePathBox.Text)
        };

        var currentFolder = Path.GetDirectoryName(DatabasePathBox.Text);
        if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
            dialog.InitialDirectory = currentFolder;

        if (dialog.ShowDialog(this) == true)
        {
            DatabasePathBox.Text = dialog.FileName;
            RefreshDatabaseStatus();
            RefreshIgnoreList();
            ReloadCharacterClassesFromCurrentPath();
        }
    }

    private async void FetchDatabase_Click(object sender, RoutedEventArgs e)
        => await FetchDatabaseAsync();

    private async Task<bool> FetchDatabaseAsync()
    {
        if (_isFetching)
            return false;

        string databasePath = DatabasePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            MessageBox.Show("The database path cannot be empty.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        string region = GetSelectedRegion();

        _isFetching = true;
        SetControlsEnabled(false);
        FetchProgressBar.Value = 0;
        FetchStatusText.Text = "Starting database update...";

        try
        {
            var database = new DatabaseService(databasePath);
            var progress = new Progress<DatabaseFetchProgress>(p =>
            {
                FetchProgressBar.Value = p.Percent;
                FetchStatusText.Text = p.Message;
            });

            await _fetchService.FetchOrUpdateAsync(
                database,
                region,
                GetSelectedLanguage(),
                progress,
                CancellationToken.None);

            FetchProgressBar.Value = 100;
            FetchStatusText.Text = "Database update complete. Icons are downloaded the first time an item is looted and then kept locally.";
            RefreshDatabaseStatus();
            RefreshIgnoreList();
            return true;
        }
        catch (Exception ex)
        {
            FetchStatusText.Text = $"Database update error: {ex.Message}";
            RefreshDatabaseStatus();

            MessageBox.Show(
                ex.Message,
                "Database update error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _isFetching = false;
            SetControlsEnabled(true);
            RefreshSpecOptions(SpecCombo.SelectedItem?.ToString());
        }
    }

    private void RefreshDatabaseStatus()
    {
        try
        {
            string path = DatabasePathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                DatabaseStatusText.Text = "Database path is not set.";
                DatabaseDetailsText.Text = string.Empty;
                return;
            }

            var database = new DatabaseService(path);
            var health = database.GetHealth(GetSelectedRegion(), GetSelectedLanguage());

            var warnings = new List<string>();

            if (!health.HasCatalog)
                warnings.Add("item database missing");
            else if (!health.HasSelectedLanguage)
                warnings.Add("selected language missing");

            if (health.MarketUpdatedUtc == null)
                warnings.Add($"{GetSelectedRegion()} loot/price data missing");
            else if (health.MarketIsStale(MarketMaxAge))
                warnings.Add($"{GetSelectedRegion()} loot/price data older than 7 days");

            DatabaseStatusText.Text = warnings.Count == 0
                ? "✓ Database is up to date"
                : "⚠ Update recommended: " + string.Join(", ", warnings);

            string catalogText = health.CatalogUpdatedUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm") ?? "never";
            string marketText = health.MarketUpdatedUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm") ?? "never";

            DatabaseDetailsText.Text =
                $"Items: {health.ItemCount:N0}  •  Selected-language names: {health.NameCount:N0}  •  " +
                $"{GetSelectedRegion()} grind loot items: {health.MarketPriceCount:N0}  •  Icons cached: {health.CachedIconCount:N0}\n" +
                $"Item DB: {catalogText}  •  Loot/Price DB: {marketText}";
        }
        catch (Exception ex)
        {
            DatabaseStatusText.Text = "Database status error";
            DatabaseDetailsText.Text = ex.Message;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_isFetching)
            return;

        if (AdapterCombo.SelectedItem is not NetworkAdapterOption adapter)
        {
            MessageBox.Show("Select a network adapter.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string databasePath = DatabasePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            MessageBox.Show("The database path cannot be empty.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string region = GetSelectedRegion();
        string language = GetSelectedLanguage();

        var database = new DatabaseService(databasePath);
        var health = database.GetHealth(region, language);

        bool updateRecommended =
            !health.HasCatalog ||
            !health.HasSelectedLanguage ||
            health.MarketIsStale(MarketMaxAge);

        if (updateRecommended)
        {
            var result = MessageBox.Show(
                "The database is incomplete for the selected Loot / Price Server / language, or the loot/price data is older than 7 days.\n\nUpdate now?",
                "Database update recommended",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
                return;

            if (result == MessageBoxResult.Yes)
            {
                bool updated = await FetchDatabaseAsync();
                if (!updated)
                    return;
            }
        }

        CharacterClassOption? selectedClass = ClassCombo.SelectedItem as CharacterClassOption;
        bool classSelected = selectedClass != null && selectedClass.ClassType >= 0;

        string startStopHotkey = NormalizeHotkeyText(StartStopHotkeyBox.Text);
        string overlayHotkey = NormalizeHotkeyText(OverlayHotkeyBox.Text);
        if (!string.Equals(startStopHotkey, "None", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(startStopHotkey, overlayHotkey, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "Start / Stop Tracking and Toggle Overlay must use different shortcuts.",
                "Keybinds",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Load the newest settings immediately before saving. Overlay placement is
        // saved independently by the placement window; using this fresh object
        // prevents the Settings dialog from overwriting a newly saved position
        // with the stale coordinates it had when the dialog was first opened.
        var settingsToSave = _settingsService.Load();
        settingsToSave.AdapterName = adapter.Name;
        settingsToSave.Region = region;
        settingsToSave.ItemLanguage = language;
        settingsToSave.CharacterClassType = classSelected ? selectedClass!.ClassType : null;
        settingsToSave.CharacterSpec = classSelected ? (SpecCombo.SelectedItem?.ToString() ?? string.Empty) : string.Empty;
        settingsToSave.CharacterName = CharacterBox.Text.Trim();
        settingsToSave.DatabasePath = databasePath;
        settingsToSave.GarmothApiKey = GarmothApiKeyBox.Password.Trim();
        settingsToSave.OverlayMode = OverlayModeCombo.SelectedItem?.ToString() == "Compact" ? "Compact" : "Detailed";
        settingsToSave.OverlayBackgroundOpacity = Math.Clamp(OverlayOpacitySlider.Value / 100.0, 0.10, 1.0);
        settingsToSave.OverlayMaxDisplayedItems = GetOverlayMaxItems();
        settingsToSave.OverlaySortBy = NormalizeOverlaySortBy(OverlaySortByCombo.SelectedItem?.ToString());
        settingsToSave.OverlaySortDescending = OverlaySortOrderCombo.SelectedItem?.ToString() != "Ascending";
        settingsToSave.StartStopHotkey = startStopHotkey;
        settingsToSave.OverlayHotkey = overlayHotkey;

        _settingsService.Save(settingsToSave);
        _settings = settingsToSave;

        DialogResult = true;
        Close();
    }

    private void RefreshIgnoreList()
    {
        try
        {
            string path = DatabasePathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                IgnoreListBox.ItemsSource = null;
                IgnoreCountText.Text = "0";
                return;
            }

            var database = new DatabaseService(path);
            var items = database.GetIgnoredItems();
            IgnoreListBox.ItemsSource = items;
            IgnoreCountText.Text = $"{items.Count:N0}";
        }
        catch
        {
            IgnoreListBox.ItemsSource = null;
            IgnoreCountText.Text = "?";
        }
    }

    private void RemoveSelectedIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (IgnoreListBox.SelectedItem is not IgnoredItemRecord item)
        {
            MessageBox.Show(
                "Select an item from the Ignore List.",
                "Ignore List",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Remove \"{item.Name}\" (ID: {item.ItemId}) from the Ignore list?",
            "Ignore List",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            var database = new DatabaseService(DatabasePathBox.Text.Trim());
            database.RemoveIgnoredItem(item.ItemId);
            RefreshIgnoreList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ignore List", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        AdapterCombo.IsEnabled = enabled;
        RefreshAdaptersButton.IsEnabled = enabled;
        RegionCombo.IsEnabled = enabled;
        LanguageCombo.IsEnabled = enabled;
        DatabasePathBox.IsEnabled = enabled;
        BrowseButton.IsEnabled = enabled;
        ClassCombo.IsEnabled = enabled;
        CharacterBox.IsEnabled = enabled;
        GarmothApiKeyBox.IsEnabled = enabled;
        OverlayModeCombo.IsEnabled = enabled;
        OverlayOpacitySlider.IsEnabled = enabled;
        OverlayMaxItemsBox.IsEnabled = enabled;
        OverlaySortByCombo.IsEnabled = enabled;
        OverlaySortOrderCombo.IsEnabled = enabled;
        MoveOverlayButton.IsEnabled = enabled;
        ResetOverlayPositionButton.IsEnabled = enabled;
        StartStopHotkeyBox.IsEnabled = enabled;
        OverlayHotkeyBox.IsEnabled = enabled;
        ClearStartStopHotkeyButton.IsEnabled = enabled;
        ClearOverlayHotkeyButton.IsEnabled = enabled;
        SpecCombo.IsEnabled = enabled &&
                              ClassCombo.SelectedItem is CharacterClassOption selected &&
                              selected.ClassType >= 0 &&
                              selected.Specs.Count > 0;
        IgnoreListBox.IsEnabled = enabled;
        RemoveIgnoreButton.IsEnabled = enabled;
        FetchButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
    }

    private void OverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayOpacityValueText != null)
            OverlayOpacityValueText.Text = $"{e.NewValue:0}%";
    }

    private void OverlayMaxItemsBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(ch => !char.IsDigit(ch));

    private void OverlayMaxItemsBox_LostFocus(object sender, RoutedEventArgs e)
        => OverlayMaxItemsBox.Text = GetOverlayMaxItems().ToString();

    private void OverlayMaxItemsUp_Click(object sender, RoutedEventArgs e)
        => OverlayMaxItemsBox.Text = Math.Min(20, GetOverlayMaxItems() + 1).ToString();

    private void OverlayMaxItemsDown_Click(object sender, RoutedEventArgs e)
        => OverlayMaxItemsBox.Text = Math.Max(1, GetOverlayMaxItems() - 1).ToString();

    private int GetOverlayMaxItems()
    {
        if (!int.TryParse(OverlayMaxItemsBox.Text, out int value))
            value = 8;

        return Math.Clamp(value, 1, 20);
    }

    public AppSettings GetOverlayPreviewSettings()
    {
        var preview = _settingsService.Load();
        preview.OverlayMode = OverlayModeCombo.SelectedItem?.ToString() == "Compact" ? "Compact" : "Detailed";
        preview.OverlayBackgroundOpacity = Math.Clamp(OverlayOpacitySlider.Value / 100.0, 0.10, 1.0);
        preview.OverlayMaxDisplayedItems = GetOverlayMaxItems();
        preview.OverlaySortBy = NormalizeOverlaySortBy(OverlaySortByCombo.SelectedItem?.ToString());
        preview.OverlaySortDescending = OverlaySortOrderCombo.SelectedItem?.ToString() != "Ascending";
        return preview;
    }

    private void MoveOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_isFetching)
            return;

        OverlayMaxItemsBox.Text = GetOverlayMaxItems().ToString();

        // Keep the Settings window in its original ShowDialog() modal state.
        // Hiding and later calling Show() turns it into a normal window, which
        // makes DialogResult throw when Save/Cancel is pressed. Minimizing it
        // keeps the modal dialog alive while leaving the screen free for overlay
        // placement.
        WindowState = WindowState.Minimized;
        MoveOverlayRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ResetOverlayPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_isFetching)
            return;

        var latest = _settingsService.Load();
        latest.OverlayLeft = 30;
        latest.OverlayTop = 80;
        latest.OverlayDetailedWidth = 390;
        latest.OverlayDetailedHeight = 540;
        latest.OverlayCompactWidth = 390;
        latest.OverlayCompactHeight = 165;
        _settingsService.Save(latest);
        _settings = latest;

        OverlayResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizeOverlaySortBy(string? value)
        => value switch
        {
            "Last Looted" => "Last Looted",
            "Total Value" => "Total Value",
            "Unit Price" => "Unit Price",
            _ => "Quantity"
        };

    private static string NormalizeHotkeyText(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "None", StringComparison.OrdinalIgnoreCase)
            ? "None"
            : value.Trim();

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if ((key == Key.Back || key == Key.Delete || key == Key.Escape) && Keyboard.Modifiers == ModifierKeys.None)
        {
            box.Text = "None";
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (modifiers == ModifierKeys.None && !isFunctionKey)
            return;

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        box.Text = string.Join("+", parts);
    }

    private void ClearStartStopHotkey_Click(object sender, RoutedEventArgs e)
        => StartStopHotkeyBox.Text = "None";

    private void ClearOverlayHotkey_Click(object sender, RoutedEventArgs e)
        => OverlayHotkeyBox.Text = "None";

    private string GetSelectedRegion()
        => DatabaseService.NormalizeRegion(RegionCombo.SelectedItem?.ToString() ?? "EU");

    private string GetSelectedLanguage()
        => DatabaseService.NormalizeLanguage(LanguageCombo.SelectedValue?.ToString() ?? "us");

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isFetching)
            return;

        DialogResult = false;
        Close();
    }
}
