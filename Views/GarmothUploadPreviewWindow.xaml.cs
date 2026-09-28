using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BDOLootTracker.Models;
using BDOLootTracker.Services;

namespace BDOLootTracker.Views;

public partial class GarmothUploadPreviewWindow : Window, INotifyPropertyChanged
{
    private readonly DatabaseService _database;
    private readonly GarmothUploadService _uploadService;
    private readonly string _apiKey;
    private readonly SessionSummary _session;
    private readonly ObservableCollection<GarmothUploadLootEditRow> _rows;
    private readonly IReadOnlyList<SpotCandidate> _spotOptions;
    private readonly ListCollectionView _spotOptionsView;
    private SpotCandidate? _selectedUploadSpot;
    private string _uploadSpotSearchText = string.Empty;
    private bool _spotSearchActive;
    private bool _isUploading;
    private int _currentUploadCount;
    private DateTime? _currentUploadedAtUtc;
    private readonly bool _gatheringCategoryDetected;

    public bool UploadedSuccessfully { get; private set; }

    public GarmothUploadPreviewWindow(
        DatabaseService database,
        GarmothUploadService uploadService,
        string apiKey,
        SessionSummary session,
        IReadOnlyCollection<SessionLootHistoryRow> loot)
    {
        InitializeComponent();
        _database = database;
        _uploadService = uploadService;
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _session = session;
        _gatheringCategoryDetected =
            string.Equals(session.SpotKey, "__gathering__", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.SpotName, "Gathering", StringComparison.OrdinalIgnoreCase);

        _spotOptions = _database.GetAllGarmothSpots();
        _spotOptionsView = new ListCollectionView(_spotOptions.ToList());
        _spotOptionsView.Filter = SpotMatchesFilter;

        _selectedUploadSpot = _gatheringCategoryDetected
            ? null
            : _spotOptions.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(session.SpotKey) &&
                 string.Equals(x.SpotKey, session.SpotKey, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(session.SpotName) &&
                 string.Equals(x.Name, session.SpotName, StringComparison.OrdinalIgnoreCase)));
        _uploadSpotSearchText = _gatheringCategoryDetected
            ? string.Empty
            : _selectedUploadSpot?.Name
                ?? (!string.IsNullOrWhiteSpace(session.SpotName) ? session.SpotName : string.Empty);
        _spotSearchActive = false;
        DataContext = this;

        _rows = new ObservableCollection<GarmothUploadLootEditRow>(loot.Select(x => new GarmothUploadLootEditRow
        {
            ItemId = x.ItemId,
            Name = x.Name,
            IconPath = x.IconPath,
            UnitPrice = x.UnitPrice,
            IsTrash = x.IsTrash,
            QuantityText = x.Quantity.ToString()
        }));

        LootGrid.ItemsSource = _rows;
        LootCountText.Text = $"{_rows.Count:N0} item(s)";
        UpdateSpotHint();

        string classText = string.IsNullOrWhiteSpace(session.ClassName)
            ? "Class —"
            : string.IsNullOrWhiteSpace(session.Spec)
                ? session.ClassName
                : $"{session.ClassName} • {session.Spec}";

        SessionMetaText.Text = $"{session.DateText}  •  {session.DurationText}  •  {classText}";
        DropRateBox.Text = session.DropRatePercent?.ToString() ?? string.Empty;
        _currentUploadCount = Math.Max(0, session.GarmothUploadCount);
        _currentUploadedAtUtc = session.GarmothUploadedAtUtc;
        RefreshUploadStatus(_currentUploadCount, _currentUploadedAtUtc);
    }

    public ListCollectionView UploadSpotOptionsView => _spotOptionsView;

    public SpotCandidate? SelectedUploadSpot
    {
        get => _selectedUploadSpot;
        set
        {
            if (ReferenceEquals(_selectedUploadSpot, value))
                return;

            _selectedUploadSpot = value;
            OnPropertyChanged();

            if (value != null)
            {
                _uploadSpotSearchText = value.Name;
                _spotSearchActive = false;
                OnPropertyChanged(nameof(UploadSpotSearchText));
                _spotOptionsView.Refresh();
            }

            UpdateSpotHint();
        }
    }

    public string UploadSpotSearchText
    {
        get => _uploadSpotSearchText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_uploadSpotSearchText, value, StringComparison.Ordinal))
                return;

            _uploadSpotSearchText = value;
            _spotSearchActive = true;

            if (_selectedUploadSpot != null &&
                !string.Equals(_selectedUploadSpot.Name, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _selectedUploadSpot = null;
                OnPropertyChanged(nameof(SelectedUploadSpot));
            }

            OnPropertyChanged();
            _spotOptionsView.Refresh();
            UpdateSpotHint();
        }
    }

    private bool SpotMatchesFilter(object item)
    {
        if (item is not SpotCandidate spot)
            return false;

        string query = (_uploadSpotSearchText ?? string.Empty).Trim();
        if (!_spotSearchActive || string.IsNullOrWhiteSpace(query))
            return true;

        if (spot.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        string normalizedQuery = NormalizeSpotSearch(query);
        string normalizedName = NormalizeSpotSearch(spot.Name);
        return normalizedQuery.Length > 0 && normalizedName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSpotSearch(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private SpotCandidate? ResolveSelectedUploadSpot()
    {
        string typed = (_uploadSpotSearchText ?? string.Empty).Trim();

        if (_selectedUploadSpot != null &&
            (string.IsNullOrWhiteSpace(typed) ||
             string.Equals(_selectedUploadSpot.Name, typed, StringComparison.OrdinalIgnoreCase)))
        {
            return _selectedUploadSpot;
        }

        if (string.IsNullOrWhiteSpace(typed))
            return null;

        return _spotOptions.FirstOrDefault(x =>
            string.Equals(x.Name, typed, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateSpotHint()
    {
        if (SpotHintText == null)
            return;

        if (_spotOptions.Count == 0)
        {
            SpotHintText.Text = "No Garmoth spots are cached. Run Settings → Database & Loot → Fetch / Update first.";
            SpotHintText.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
            return;
        }

        SpotCandidate? selected = ResolveSelectedUploadSpot();
        if (selected != null)
        {
            SpotHintText.Text = $"Selected: {selected.Name}. The choice is saved to this session when you upload.";
            SpotHintText.Foreground = (System.Windows.Media.Brush)FindResource("Green");
            return;
        }

        int visible = _spotOptionsView.Cast<object>().Count();
        if (_gatheringCategoryDetected && string.IsNullOrWhiteSpace(_uploadSpotSearchText))
        {
            SpotHintText.Text =
                $"Gathering was detected automatically. Choose the exact Garmoth gathering spot before upload ({_spotOptions.Count:N0} spots available).";
        }
        else
        {
            SpotHintText.Text = string.IsNullOrWhiteSpace(_uploadSpotSearchText)
                ? $"{_spotOptions.Count:N0} Garmoth spot(s) available — start typing to search."
                : $"{visible:N0} matching Garmoth spot(s). Select an exact result before upload.";
        }
        SpotHintText.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
    }

    private void SpotComboBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is ComboBox combo && combo.IsEditable && combo.IsEnabled)
            combo.IsDropDownOpen = true;
    }

    private void SpotComboBox_KeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox combo || !combo.IsEditable || !combo.IsEnabled)
            return;

        if (e.Key is Key.Escape or Key.Enter or Key.Tab)
            return;

        combo.IsDropDownOpen = true;
    }

    private void RefreshUploadStatus(int uploadCount, DateTime? uploadedAtUtc)
    {
        if (uploadCount <= 0 && uploadedAtUtc == null)
        {
            AlreadyUploadedText.Visibility = Visibility.Collapsed;
            UploadStateText.Text = "Not uploaded";
            UploadStateText.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
            UploadButton.Content = "Upload to Garmoth";
            return;
        }

        int count = Math.Max(1, uploadCount);
        string when = uploadedAtUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm") ?? "previously";
        AlreadyUploadedText.Text = $"⚠ This session has already been uploaded to Garmoth {count}x. Last upload: {when}. Uploading again may create a duplicate entry.";
        AlreadyUploadedText.Visibility = Visibility.Visible;
        UploadStateText.Text = $"Uploaded {count}x";
        UploadStateText.Foreground = (System.Windows.Media.Brush)FindResource("Green");
        UploadButton.Content = "Upload again";
    }

    private int? ReadDropRate()
    {
        string text = DropRateBox.Text.Trim().Replace("%", string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!int.TryParse(text, out int value) || value < 0 || value > 5000)
            throw new InvalidOperationException("Drop Rate must be a whole number between 0 and 5000, or left empty.");

        return value == 0 ? null : value;
    }

    private IReadOnlyList<SessionLootHistoryRow> BuildUploadLoot()
    {
        var result = new List<SessionLootHistoryRow>();
        foreach (GarmothUploadLootEditRow row in _rows)
        {
            if (!row.TryGetQuantity(out ulong quantity))
                throw new InvalidOperationException($"Invalid quantity for {row.Name}. Use a whole number only.");

            if (quantity == 0)
                continue;

            result.Add(new SessionLootHistoryRow
            {
                ItemId = row.ItemId,
                Name = row.Name,
                IconPath = row.IconPath,
                Quantity = quantity,
                UnitPrice = row.UnitPrice,
                IsTrash = row.IsTrash,
                IsIgnored = false
            });
        }

        if (result.Count == 0)
            throw new InvalidOperationException("At least one loot item must have a quantity greater than zero.");

        return result;
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_isUploading)
            return;

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            AppDialog.Show(
                "No Garmoth API token is saved. Open Settings → Garmoth and add the token first.",
                "Garmoth Upload",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            SpotCandidate? selectedSpot = ResolveSelectedUploadSpot();
            if (selectedSpot == null || string.IsNullOrWhiteSpace(selectedSpot.SpotKey))
            {
                throw new InvalidOperationException(
                    "Select an exact grind spot from the Garmoth list before uploading. You can type any part of the spot name to search.");
            }

            int? dropRate = ReadDropRate();
            IReadOnlyList<SessionLootHistoryRow> uploadLoot = BuildUploadLoot();

            // Keep the correction with the stored session as well. This is useful for
            // gathering/ambiguous spots where automatic detection cannot be reliable.
            _database.UpdateSessionSpot(_session.SessionId, selectedSpot.SpotKey, selectedSpot.Name);
            SessionSummary uploadSession = CloneSessionWithSpot(_session, selectedSpot);

            if (_currentUploadCount > 0 || _currentUploadedAtUtc != null)
            {
                MessageBoxResult duplicate = AppDialog.Show(
                    "This session has already been uploaded to Garmoth. Uploading it again may create a duplicate session.\n\nContinue anyway?",
                    "Duplicate Garmoth Upload",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (duplicate != MessageBoxResult.Yes)
                    return;
            }

            _isUploading = true;
            UploadButton.IsEnabled = false;
            UploadButton.Content = "Uploading...";
            StatusText.Text = "Sending session to Garmoth...";

            GarmothUploadService.UploadResult result = await _uploadService.UploadSessionAsync(
                _apiKey,
                uploadSession,
                uploadLoot,
                dropRate,
                CancellationToken.None);

            // The remote upload has succeeded at this point. Mark that fact before
            // touching local metadata so a local SQLite write problem can never be
            // misreported as a failed Garmoth request and tempt a duplicate retry.
            UploadedSuccessfully = true;
            _currentUploadedAtUtc = DateTime.UtcNow;
            _currentUploadCount = Math.Max(0, _currentUploadCount) + 1;
            RefreshUploadStatus(_currentUploadCount, _currentUploadedAtUtc);

            string localMetadataWarning = string.Empty;
            try
            {
                _database.MarkSessionGarmothUploaded(_session.SessionId, dropRate);
            }
            catch (Exception metadataEx)
            {
                localMetadataWarning = $"\n\nThe Garmoth upload succeeded, but the local upload marker could not be saved: {metadataEx.Message}";
            }

            string dropRateMessage = result.DropRateRequested
                ? " Drop Rate is saved locally with this session; Garmoth's external upload API does not currently apply that value."
                : string.Empty;

            StatusText.Text = "Upload completed successfully.";
            AppDialog.Show(
                "Session uploaded to Garmoth successfully." + dropRateMessage + localMetadataWarning,
                "Garmoth Upload",
                MessageBoxButton.OK,
                string.IsNullOrEmpty(localMetadataWarning) ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Garmoth upload failed.";
            AppDialog.Show(ex.Message, "Garmoth Upload Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isUploading = false;
            UploadButton.IsEnabled = true;
            if (!UploadedSuccessfully)
                UploadButton.Content = (_currentUploadCount > 0 || _currentUploadedAtUtc != null) ? "Upload again" : "Upload to Garmoth";
        }
    }

    private static SessionSummary CloneSessionWithSpot(SessionSummary source, SpotCandidate spot)
    {
        return new SessionSummary
        {
            SessionId = source.SessionId,
            StartedAtUtc = source.StartedAtUtc,
            EffectiveEndUtc = source.EffectiveEndUtc,
            IsCompleted = source.IsCompleted,
            Region = source.Region,
            CharacterName = source.CharacterName,
            ClassType = source.ClassType,
            ClassName = source.ClassName,
            Spec = source.Spec,
            SpotKey = spot.SpotKey,
            SpotName = spot.Name,
            TotalSilver = source.TotalSilver,
            TotalTrash = source.TotalTrash,
            GarmothUploadedAtUtc = source.GarmothUploadedAtUtc,
            GarmothUploadCount = source.GarmothUploadCount,
            DropRatePercent = source.DropRatePercent,
            ActiveDuration = source.ActiveDuration
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
