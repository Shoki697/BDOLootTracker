using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using BDOLootTracker.Models;
using BDOLootTracker.Services;

namespace BDOLootTracker.Views;

public partial class GarmothUploadPreviewWindow : Window
{
    private readonly DatabaseService _database;
    private readonly GarmothUploadService _uploadService;
    private readonly string _apiKey;
    private readonly SessionSummary _session;
    private readonly ObservableCollection<GarmothUploadLootEditRow> _rows;
    private bool _isUploading;
    private int _currentUploadCount;
    private DateTime? _currentUploadedAtUtc;

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
        SpotText.Text = string.IsNullOrWhiteSpace(session.SpotName) ? "Unknown grind spot" : session.SpotName;

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
            int? dropRate = ReadDropRate();
            IReadOnlyList<SessionLootHistoryRow> uploadLoot = BuildUploadLoot();

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
                _session,
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
