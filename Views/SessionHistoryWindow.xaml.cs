using System.Windows;
using System.Windows.Controls;
using BDOLootTracker.Models;
using BDOLootTracker.Services;

namespace BDOLootTracker.Views;

public partial class SessionHistoryWindow : Window
{
    private readonly DatabaseService _database;
    private readonly SettingsService _settingsService;
    private readonly GarmothUploadService _garmothUploadService;
    private readonly string _language;
    private readonly long _activeSessionId;
    private bool _loaded;
    private bool _isUploading;

    public SessionHistoryWindow(
        DatabaseService database,
        SettingsService settingsService,
        string language,
        long activeSessionId = 0)
    {
        InitializeComponent();
        _database = database;
        _settingsService = settingsService;
        _garmothUploadService = new GarmothUploadService(_database);
        _language = language;
        _activeSessionId = activeSessionId;

        Loaded += (_, _) =>
        {
            _loaded = true;
            LoadSessions();
        };

        Closed += (_, _) => _garmothUploadService.Dispose();
    }

    private bool HideIgnored => HideIgnoredCheck.IsChecked == true;

    private void LoadSessions(long? selectSessionId = null)
    {
        try
        {
            long? previousId = selectSessionId ?? (SessionGrid.SelectedItem as SessionSummary)?.SessionId;
            var sessions = _database.GetSessions(_language, HideIgnored);
            SessionGrid.ItemsSource = sessions;
            SessionCountText.Text = $"{sessions.Count:N0}";

            SessionSummary? toSelect = null;
            if (previousId != null)
                toSelect = sessions.FirstOrDefault(x => x.SessionId == previousId.Value);

            toSelect ??= sessions.FirstOrDefault();
            SessionGrid.SelectedItem = toSelect;

            if (toSelect == null)
                ClearDetails();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Session history", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadSelectedSession()
    {
        if (SessionGrid.SelectedItem is not SessionSummary session)
        {
            ClearDetails();
            return;
        }

        try
        {
            var loot = _database.GetSessionLoot(session.SessionId, _language, HideIgnored);
            LootGrid.ItemsSource = loot;

            DetailTitleText.Text = session.DateText;

            var detailParts = new List<string> { session.Region };

            if (!string.IsNullOrWhiteSpace(session.SpotName))
                detailParts.Add(session.SpotName);

            if (!string.IsNullOrWhiteSpace(session.ClassName))
            {
                detailParts.Add(string.IsNullOrWhiteSpace(session.Spec)
                    ? session.ClassName
                    : $"{session.ClassName} • {session.Spec}");
            }

            if (!string.IsNullOrWhiteSpace(session.CharacterName))
                detailParts.Add($"Character: {session.CharacterName}");

            DetailSubtitleText.Text = string.Join("  •  ", detailParts);
            DetailStatusText.Text = session.SessionId == _activeSessionId
                ? "ACTIVE SESSION"
                : session.StatusText;
            DetailDurationText.Text = session.DurationText;
            DetailTotalSilverText.Text = session.TotalSilverText;
            DetailSilverHrText.Text = session.SilverPerHourText;
            DetailTrashHrText.Text = session.TrashPerHourText;
            InfoText.Text = $"{loot.Count:N0} visible item(s)";
            UpdateUploadButtonState(session);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Session details", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearDetails()
    {
        LootGrid.ItemsSource = null;
        DetailTitleText.Text = "No sessions yet";
        DetailSubtitleText.Text = string.Empty;
        DetailStatusText.Text = string.Empty;
        DetailDurationText.Text = "—";
        DetailTotalSilverText.Text = "—";
        DetailSilverHrText.Text = "—";
        DetailTrashHrText.Text = "—";
        InfoText.Text = string.Empty;
        UploadGarmothButton.IsEnabled = false;
        UploadGarmothButton.Content = "☁  Upload to Garmoth";
    }

    private void SessionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => LoadSelectedSession();

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => LoadSessions();

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            LoadSessions();
    }

    private void IgnoreItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not SessionLootHistoryRow row)
        {
            return;
        }

        if (row.IsIgnored)
        {
            MessageBox.Show(
                "This item is already in the Ignore List.",
                "Ignore item",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Add \"{row.Name}\" (ID: {row.ItemId}) to the Ignore list?\n\n" +
            "This item will not appear in future sessions and will not be included in calculations.\n" +
            "Existing session data will not be deleted; use 'Hide ignored items' to hide it.",
            "Add to Ignore List",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            _database.AddIgnoredItem(row.ItemId, row.Name);
            long? selectedId = (SessionGrid.SelectedItem as SessionSummary)?.SessionId;
            LoadSessions(selectedId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ignore item", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateUploadButtonState(SessionSummary? session)
    {
        if (_isUploading)
        {
            UploadGarmothButton.IsEnabled = false;
            UploadGarmothButton.Content = "Uploading...";
            return;
        }

        UploadGarmothButton.Content = "☁  Upload to Garmoth";
        UploadGarmothButton.IsEnabled =
            session != null &&
            session.SessionId != _activeSessionId &&
            session.IsCompleted;
    }

    private async void UploadToGarmoth_Click(object sender, RoutedEventArgs e)
    {
        if (_isUploading || SessionGrid.SelectedItem is not SessionSummary session)
            return;

        if (session.SessionId == _activeSessionId || !session.IsCompleted)
        {
            MessageBox.Show(
                "Only completed sessions can be uploaded. Stop the tracker first.",
                "Upload to Garmoth",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        AppSettings settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.GarmothApiKey))
        {
            MessageBox.Show(
                "No Garmoth API token is saved. Open Settings, paste your Garmoth API token, and press Save.",
                "Upload to Garmoth",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Ignore List entries are always excluded from uploads, regardless of
        // the current 'Hide ignored items' visual filter.
        IReadOnlyList<SessionLootHistoryRow> uploadLoot;
        try
        {
            uploadLoot = _database.GetSessionLoot(session.SessionId, _language, hideIgnored: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Upload to Garmoth", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (uploadLoot.Count == 0)
        {
            MessageBox.Show(
                "The selected session has no uploadable loot items.",
                "Upload to Garmoth",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string classText = string.IsNullOrWhiteSpace(session.ClassName)
            ? "—"
            : string.IsNullOrWhiteSpace(session.Spec)
                ? session.ClassName
                : $"{session.ClassName} • {session.Spec}";

        string spotText = string.IsNullOrWhiteSpace(session.SpotName) ? "—" : session.SpotName;

        var confirmation = MessageBox.Show(
            $"Upload this session to Garmoth?\n\n" +
            $"Date: {session.DateText}\n" +
            $"Spot: {spotText}\n" +
            $"Class: {classText}\n" +
            $"Duration: {session.DurationText}\n" +
            $"Loot items: {uploadLoot.Count:N0}\n\n" +
            "Uploading the same session more than once may create a duplicate entry on Garmoth.",
            "Upload to Garmoth",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
            return;

        _isUploading = true;
        UpdateUploadButtonState(session);
        InfoText.Text = "Uploading session to Garmoth...";

        try
        {
            await _garmothUploadService.UploadSessionAsync(
                settings.GarmothApiKey,
                session,
                uploadLoot,
                CancellationToken.None);

            InfoText.Text = "Upload to Garmoth completed successfully.";
            MessageBox.Show(
                "Session uploaded to Garmoth successfully.",
                "Upload to Garmoth",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            InfoText.Text = "Garmoth upload failed.";
            MessageBox.Show(
                ex.Message,
                "Garmoth upload error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isUploading = false;
            UpdateUploadButtonState(SessionGrid.SelectedItem as SessionSummary);
        }
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionGrid.SelectedItem is not SessionSummary session)
            return;

        if (session.SessionId == _activeSessionId)
        {
            MessageBox.Show(
                "The active session cannot be deleted. Stop the tracker first.",
                "Delete Session",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Delete session from {session.DateText}?\n\nThis permanently deletes the session and its associated loot list.",
            "Delete Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            _database.DeleteSession(session.SessionId);
            LoadSessions();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Delete Session", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
