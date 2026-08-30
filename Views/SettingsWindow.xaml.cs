using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        Closing += SettingsWindow_Closing;
        Closed += (_, _) =>
        {
            _classIconService?.Dispose();
            _fetchService.Dispose();
        };
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

        _settings.AdapterName = adapter.Name;
        _settings.Region = region;
        _settings.ItemLanguage = language;
        _settings.CharacterClassType = classSelected ? selectedClass!.ClassType : null;
        _settings.CharacterSpec = classSelected ? (SpecCombo.SelectedItem?.ToString() ?? string.Empty) : string.Empty;
        _settings.CharacterName = CharacterBox.Text.Trim();
        _settings.DatabasePath = databasePath;
        _settings.GarmothApiKey = GarmothApiKeyBox.Password.Trim();

        _settingsService.Save(_settings);

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
