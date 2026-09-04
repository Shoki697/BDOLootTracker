using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private IReadOnlyList<SessionSummary> _allSessions = Array.Empty<SessionSummary>();
    private bool _loaded;
    private bool _refreshing;

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

    private void LoadSessions(string? preferredSpotKey = null)
    {
        try
        {
            _refreshing = true;
            string previousKey = preferredSpotKey
                ?? (SpotList.SelectedItem as SpotFilterItem)?.Key
                ?? string.Empty;

            _allSessions = _database.GetSessions(_language, HideIgnored, limit: 1000);

            var spots = new List<SpotFilterItem>
            {
                new(string.Empty, "All Spots", _allSessions.Count)
            };

            spots.AddRange(_allSessions
                .GroupBy(GetSpotGroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SpotFilterItem(
                    g.Key,
                    GetSpotDisplayName(g.First()),
                    g.Count()))
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase));

            SpotList.ItemsSource = spots;
            SpotFilterItem selected = spots.FirstOrDefault(x => string.Equals(x.Key, previousKey, StringComparison.OrdinalIgnoreCase))
                ?? spots[0];
            SpotList.SelectedItem = selected;
            _refreshing = false;
            ApplySpotFilter();
        }
        catch (Exception ex)
        {
            _refreshing = false;
            AppDialog.Show(ex.Message, "Session History", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplySpotFilter()
    {
        SpotFilterItem selected = SpotList.SelectedItem as SpotFilterItem
            ?? new SpotFilterItem(string.Empty, "All Spots", _allSessions.Count);

        List<SessionSummary> sessions = string.IsNullOrEmpty(selected.Key)
            ? _allSessions.ToList()
            : _allSessions.Where(x => string.Equals(GetSpotGroupKey(x), selected.Key, StringComparison.OrdinalIgnoreCase)).ToList();

        var cards = new List<SessionCardViewModel>(sessions.Count);
        foreach (SessionSummary session in sessions)
        {
            IReadOnlyList<SessionLootHistoryRow> preview;
            try
            {
                preview = _database.GetSessionLoot(session.SessionId, _language, HideIgnored)
                    .OrderByDescending(x => x.IsTrash)
                    .ThenByDescending(x => x.TotalSilver)
                    .ThenByDescending(x => x.Quantity)
                    .Take(8)
                    .ToList();
            }
            catch
            {
                preview = Array.Empty<SessionLootHistoryRow>();
            }

            cards.Add(new SessionCardViewModel(session, preview, session.SessionId == _activeSessionId));
        }

        SessionCardsControl.ItemsSource = cards;
        SelectedSpotTitleText.Text = selected.Name;
        SessionCountText.Text = $"• {sessions.Count:N0} session(s)";
        UpdateSummary(sessions);
    }

    private void UpdateSummary(IReadOnlyCollection<SessionSummary> sessions)
    {
        decimal totalSilver = sessions.Sum(x => x.TotalSilver);
        double totalHours = sessions.Sum(x => x.Duration.TotalHours);
        decimal totalTrash = sessions.Sum(x => (decimal)x.TotalTrash);

        decimal silverPerHour = totalHours > 0.0001 ? totalSilver / (decimal)totalHours : 0;
        decimal trashPerHour = totalHours > 0.0001 ? totalTrash / (decimal)totalHours : 0;
        TimeSpan totalTime = TimeSpan.FromHours(totalHours);

        TotalSilverText.Text = FormatCompact(totalSilver);
        SilverHrText.Text = FormatCompact(silverPerHour);
        TrashHrText.Text = trashPerHour <= 0 ? "—" : $"{trashPerHour:N0}";
        TotalTimeText.Text = FormatDuration(totalTime);
    }

    private static string GetSpotGroupKey(SessionSummary session)
    {
        if (!string.IsNullOrWhiteSpace(session.SpotName))
            return "name:" + NormalizeSpotName(session.SpotName);
        if (!string.IsNullOrWhiteSpace(session.SpotKey))
            return "key:" + session.SpotKey.Trim().ToLowerInvariant();
        return "unknown";
    }

    private static string GetSpotDisplayName(SessionSummary session)
        => !string.IsNullOrWhiteSpace(session.SpotName) ? session.SpotName : "Unknown / Unassigned";

    private static string NormalizeSpotName(string value)
        => string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string FormatCompact(decimal value)
    {
        decimal abs = Math.Abs(value);
        if (abs >= 1_000_000_000m)
            return $"{value / 1_000_000_000m:0.##} B";
        if (abs >= 1_000_000m)
            return $"{value / 1_000_000m:0.##} M";
        if (abs >= 1_000m)
            return $"{value / 1_000m:0.##} K";
        return $"{value:N0}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}h {value.Minutes:00}m";
        return $"{value.Minutes}m {value.Seconds:00}s";
    }

    private void EnsureFullLootLoaded(SessionCardViewModel card, bool hideIgnored)
    {
        if (card.FullLootLoaded && hideIgnored == HideIgnored)
            return;

        IReadOnlyList<SessionLootHistoryRow> loot = _database.GetSessionLoot(card.Session.SessionId, _language, hideIgnored);
        card.SetFullLoot(loot);
    }

    private void ToggleSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionCardViewModel card })
            return;

        try
        {
            if (!card.IsExpanded)
                EnsureFullLootLoaded(card, HideIgnored);
            card.IsExpanded = !card.IsExpanded;
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Session Details", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UploadSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionCardViewModel card })
            return;

        SessionSummary session = card.Session;
        if (session.SessionId == _activeSessionId || !session.IsCompleted)
        {
            AppDialog.Show("Only completed sessions can be uploaded. Stop the tracker first.", "Garmoth Upload", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AppSettings settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.GarmothApiKey))
        {
            AppDialog.Show("No Garmoth API token is saved. Open Settings → Garmoth and add the token first.", "Garmoth Upload", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            IReadOnlyList<SessionLootHistoryRow> loot = _database.GetSessionLoot(session.SessionId, _language, hideIgnored: true);
            if (loot.Count == 0)
            {
                AppDialog.Show("The selected session has no uploadable loot items.", "Garmoth Upload", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var preview = new GarmothUploadPreviewWindow(_database, _garmothUploadService, settings.GarmothApiKey, session, loot)
            {
                Owner = this
            };
            preview.ShowDialog();
            if (preview.UploadedSuccessfully)
                LoadSessions((SpotList.SelectedItem as SpotFilterItem)?.Key);
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Garmoth Upload", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void IgnoreItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not SessionLootHistoryRow row)
            return;

        if (row.IsIgnored)
        {
            AppDialog.Show("This item is already in the Ignore List.", "Ignore Item", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult result = AppDialog.Show(
            $"Add \"{row.Name}\" (ID: {row.ItemId}) to the Ignore List?\n\nFuture sessions and calculations will exclude this item. Existing stored session data is not deleted.",
            "Add to Ignore List",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            _database.AddIgnoredItem(row.ItemId, row.Name);
            LoadSessions((SpotList.SelectedItem as SpotFilterItem)?.Key);
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Ignore Item", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionCardViewModel card })
            return;

        SessionSummary session = card.Session;
        if (session.SessionId == _activeSessionId)
        {
            AppDialog.Show("The active session cannot be deleted. Stop the tracker first.", "Delete Session", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult result = AppDialog.Show(
            $"Delete the session from {session.DateText}?\n\nThis permanently deletes the session and its associated loot list.",
            "Delete Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            _database.DeleteSession(session.SessionId);
            LoadSessions((SpotList.SelectedItem as SpotFilterItem)?.Key);
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Delete Session", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveDropRate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionCardViewModel card })
            return;

        try
        {
            string raw = (card.DropRateEditText ?? string.Empty).Trim().Replace("%", string.Empty);
            int? dropRate = null;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (!int.TryParse(raw, out int value) || value < 0 || value > 5000)
                    throw new InvalidOperationException("Drop Rate must be a whole number between 0 and 5000, or left empty.");

                if (value > 0)
                    dropRate = value;
            }

            _database.UpdateSessionDropRate(card.Session.SessionId, dropRate);
            LoadSessions((SpotList.SelectedItem as SpotFilterItem)?.Key);
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Drop Rate", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GenerateScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionCardViewModel card })
            return;

        try
        {
            IReadOnlyList<SessionLootHistoryRow> loot = _database.GetSessionLoot(card.Session.SessionId, _language, hideIgnored: HideIgnored);
            if (loot.Count == 0)
            {
                AppDialog.Show("This session does not contain any visible loot to place on the screenshot.", "Session Screenshot", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BitmapSource bitmap = RenderSessionScreenshot(card.Session, loot);
            string safeSpot = string.Concat(GetSpotDisplayName(card.Session).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            string suggestedName = $"BDOLootTracker_{safeSpot}_{card.Session.StartedAtUtc.ToLocalTime():yyyyMMdd_HHmm}.png";

            var preview = new SessionScreenshotPreviewWindow(bitmap, suggestedName)
            {
                Owner = this
            };
            preview.ShowDialog();
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Session Screenshot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static BitmapSource RenderSessionScreenshot(SessionSummary session, IReadOnlyList<SessionLootHistoryRow> loot)
    {
        FrameworkElement visual = BuildShareCard(session, loot);
        const double width = 560;
        visual.Measure(new Size(width, double.PositiveInfinity));
        double height = Math.Max(320, Math.Ceiling(visual.DesiredSize.Height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();

        const double renderScale = 2.0;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * renderScale),
            (int)Math.Ceiling(height * renderScale),
            96 * renderScale,
            96 * renderScale,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static FrameworkElement BuildShareCard(SessionSummary session, IReadOnlyList<SessionLootHistoryRow> loot)
    {
        Brush bg = ResourceBrush("Bg", Brushes.Black);
        Brush panel = ResourceBrush("Panel", new SolidColorBrush(Color.FromRgb(17, 25, 34)));
        Brush borderBrush = ResourceBrush("Border", Brushes.DimGray);
        Brush text = ResourceBrush("Text", Brushes.White);
        Brush muted = ResourceBrush("Muted", Brushes.LightGray);
        Brush green = ResourceBrush("Green", Brushes.LimeGreen);
        Brush accent = ResourceBrush("Accent", Brushes.DeepSkyBlue);

        var root = new Border
        {
            Width = 560,
            Background = bg,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18),
            Child = new StackPanel()
        };
        var stack = (StackPanel)root.Child;

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        ImageSource? appIcon = TryLoadPackImage("pack://application:,,,/Resources/HeaderLogo.png");
        if (appIcon != null)
        {
            brand.Children.Add(new Image
            {
                Source = appIcon,
                Width = 40,
                Height = 40,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 10, 0)
            });
        }
        brand.Children.Add(new TextBlock
        {
            Text = "BDO LOOT TRACKER",
            Foreground = accent,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(brand);

        stack.Children.Add(new TextBlock
        {
            Text = GetSpotDisplayName(session),
            Foreground = text,
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0)
        });

        string classText = string.IsNullOrWhiteSpace(session.ClassName)
            ? "Class —"
            : string.IsNullOrWhiteSpace(session.Spec) ? session.ClassName : $"{session.ClassName} • {session.Spec}";
        stack.Children.Add(new TextBlock
        {
            Text = $"{session.DateText}  •  {session.DurationText}  •  {classText}",
            Foreground = muted,
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 12)
        });

        var summary = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddSummaryCell(summary, 0, 0, "TOTAL SILVER", FormatCompact(session.TotalSilver), text, muted, panel, borderBrush);
        AddSummaryCell(summary, 1, 0, "SILVER / HR", FormatCompact(session.SilverPerHour), green, muted, panel, borderBrush);
        AddSummaryCell(summary, 0, 1, "TRASH / HR", session.TotalTrash == 0 ? "—" : $"{session.TrashPerHour:N0}", accent, muted, panel, borderBrush);
        AddSummaryCell(summary, 1, 1, "DROP RATE", session.DropRatePercent.HasValue ? $"{session.DropRatePercent}%" : "—", text, muted, panel, borderBrush);
        stack.Children.Add(summary);

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = borderBrush,
            Margin = new Thickness(0, 0, 0, 10),
            Opacity = 0.6
        });

        stack.Children.Add(new TextBlock
        {
            Text = "ACQUIRED LOOT",
            Foreground = muted,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var lootWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 58,
            Margin = new Thickness(0, 0, 0, 4)
        };

        foreach (SessionLootHistoryRow row in loot)
        {
            var slot = new Border
            {
                Width = 48,
                Height = 48,
                Margin = new Thickness(0, 0, 8, 8),
                Background = panel,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                ToolTip = $"{row.Name} {row.QuantityText}"
            };

            var slotGrid = new Grid
            {
                Margin = new Thickness(1)
            };

            ImageSource? source = TryLoadImage(row.IconPath);
            if (source != null)
            {
                slotGrid.Children.Add(new Image
                {
                    Source = source,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(1)
                });
            }

            var qtyBadge = new Border
            {
                MinWidth = 34,
                MaxWidth = 42,
                Height = 15,
                Background = new SolidColorBrush(Color.FromArgb(150, 6, 11, 17)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 2)
            };
            qtyBadge.Child = new TextBlock
            {
                Text = FormatLootBadgeQuantity(row.Quantity),
                Foreground = text,
                FontSize = 8.7,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            slotGrid.Children.Add(qtyBadge);
            slot.Child = slotGrid;
            lootWrap.Children.Add(slot);
        }
        stack.Children.Add(lootWrap);

        stack.Children.Add(new TextBlock
        {
            Text = $"Generated by BDO Loot Tracker  •  {DateTime.Now:yyyy.MM.dd HH:mm}",
            Foreground = muted,
            FontSize = 9,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        });

        return root;
    }

    private static void AddSummaryCell(Grid grid, int column, int row, string label, string value, Brush valueBrush, Brush muted, Brush panel, Brush border)
    {
        var box = new Border
        {
            Background = panel,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(column == 0 ? 0 : 4, row == 0 ? 0 : 4, column == 1 ? 0 : 4, row == 1 ? 0 : 4)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, Foreground = muted, FontSize = 10 });
        stack.Children.Add(new TextBlock { Text = value, Foreground = valueBrush, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0) });
        box.Child = stack;
        Grid.SetColumn(box, column);
        Grid.SetRow(box, row);
        grid.Children.Add(box);
    }

    private static string FormatLootBadgeQuantity(ulong quantity)
    {
        if (quantity < 100_000UL)
            return $"x{quantity:N0}";
        if (quantity < 1_000_000UL)
            return $"x{quantity / 1_000d:0.#}K";
        if (quantity < 1_000_000_000UL)
            return $"x{quantity / 1_000_000d:0.#}M";
        return $"x{quantity / 1_000_000_000d:0.#}B";
    }

    private static Brush ResourceBrush(string key, Brush fallback)
        => Application.Current?.Resources[key] as Brush ?? fallback;

    private static ImageSource? TryLoadImage(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryLoadPackImage(string uri)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(uri, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void SpotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && !_refreshing)
            ApplySpotFilter();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => LoadSessions((SpotList.SelectedItem as SpotFilterItem)?.Key);

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            LoadSessions((SpotList.SelectedItem as SpotFilterItem)?.Key);
    }

    private sealed record SpotFilterItem(string Key, string Name, int Count);

    private sealed class SessionCardViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private IReadOnlyList<SessionLootHistoryRow> _fullLoot = Array.Empty<SessionLootHistoryRow>();

        public SessionSummary Session { get; }
        public IReadOnlyList<SessionLootHistoryRow> PreviewLoot { get; }
        public bool IsActive { get; }
        public bool FullLootLoaded { get; private set; }

        public SessionCardViewModel(SessionSummary session, IReadOnlyList<SessionLootHistoryRow> previewLoot, bool isActive)
        {
            Session = session;
            PreviewLoot = previewLoot;
            IsActive = isActive;
            DropRateEditText = session.DropRatePercent?.ToString() ?? string.Empty;
        }

        public string DropRateEditText { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                    return;
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExpandGlyph));
            }
        }

        public IReadOnlyList<SessionLootHistoryRow> FullLoot
        {
            get => _fullLoot;
            private set
            {
                _fullLoot = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExpandedInfoText));
            }
        }

        public void SetFullLoot(IReadOnlyList<SessionLootHistoryRow> loot)
        {
            FullLoot = loot;
            FullLootLoaded = true;
        }

        public string HeaderText
        {
            get
            {
                string classText = string.IsNullOrWhiteSpace(Session.ClassName)
                    ? "Class —"
                    : string.IsNullOrWhiteSpace(Session.Spec) ? Session.ClassName : $"{Session.ClassName} {Session.Spec}";
                return $"{Session.DateText}  •  {Session.DurationText}  •  {classText}";
            }
        }

        public string SpotText => GetSpotDisplayName(Session);
        public string SilverHrText => $"{FormatCompact(Session.SilverPerHour)}/hr";
        public string TrashHrText => Session.TotalTrash == 0 ? "Trash —" : $"Trash {Session.TrashPerHour:N0}/hr";
        public string TotalSilverText => $"Total {FormatCompact(Session.TotalSilver)}";
        public string DurationText => Session.DurationText;
        public string CharacterText => string.IsNullOrWhiteSpace(Session.CharacterName) ? "—" : Session.CharacterName;
        public string DropRateText => Session.DropRatePercent.HasValue ? $"{Session.DropRatePercent}%" : "—";
        public string ExpandGlyph => IsExpanded ? "▲" : "▼";
        public string ActivityStatusText => IsActive ? "ACTIVE" : string.Empty;
        public string GarmothStatusText => Session.IsUploadedToGarmoth ? $"✓ Garmoth {Math.Max(1, Session.GarmothUploadCount)}x" : "Not uploaded";
        public Brush GarmothStatusBrush => Session.IsUploadedToGarmoth ? ResourceBrush("Green", Brushes.LimeGreen) : ResourceBrush("Muted", Brushes.Gray);
        public string GarmothDetailText => Session.IsUploadedToGarmoth
            ? $"{Math.Max(1, Session.GarmothUploadCount)} upload(s) • {Session.GarmothUploadedAtText}"
            : "Not uploaded";
        public string ExpandedInfoText => FullLootLoaded ? $"{FullLoot.Count:N0} visible loot item(s)" : "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
