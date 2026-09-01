using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BDOLootTracker.Models;
using BDOLootTracker.Services;

namespace BDOLootTracker.Views;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly SettingsService _settingsService;
    private AppSettings _settings;
    private readonly bool _editMode;
    private string _mode = "Detailed";
    private double _aspectRatio = 380.0 / 500.0;

    private double _originalLeft;
    private double _originalTop;
    private double _originalWidth;
    private double _originalHeight;

    public event Action<bool>? PlacementFinished;

    public OverlayWindow(
        object dataContext,
        SettingsService settingsService,
        AppSettings settings,
        bool editMode = false)
    {
        InitializeComponent();

        DataContext = dataContext;
        _settingsService = settingsService;
        _settings = settings;
        _editMode = editMode;

        ApplySettings(settings);

        SourceInitialized += (_, _) => ApplyWindowInteractionStyle(editMode: _editMode);
        Loaded += (_, _) =>
        {
            KeepWindowOnVirtualDesktop();

            if (_editMode)
                BeginPlacementMode();
        };
    }

    private void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _mode = string.Equals(settings.OverlayMode, "Compact", StringComparison.OrdinalIgnoreCase)
            ? "Compact"
            : "Detailed";

        int maxItems = Math.Clamp(settings.OverlayMaxDisplayedItems, 1, 20);

        DetailedDesign.Visibility = _mode == "Detailed"
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactDesign.Visibility = _mode == "Compact"
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Give every requested row a place in the design. The complete design is
        // rendered through a Viewbox and the window itself is kept at the same
        // aspect ratio, so resizing can never stretch or squash the overlay.
        DetailedDesign.Height = 205 + (maxItems * 36);

        double designWidth;
        double designHeight;
        double savedWidth;
        double fallbackWidth;
        double minimumWidth;

        if (_mode == "Compact")
        {
            designWidth = CompactDesign.Width;
            designHeight = CompactDesign.Height;
            savedWidth = settings.OverlayCompactWidth;
            fallbackWidth = 390;
            minimumWidth = 280;
        }
        else
        {
            designWidth = DetailedDesign.Width;
            designHeight = DetailedDesign.Height;
            savedWidth = settings.OverlayDetailedWidth;
            fallbackWidth = 390;
            minimumWidth = 300;
        }

        _aspectRatio = designWidth / designHeight;
        ConfigureAspectRatioLimits(minimumWidth);
        ApplyAspectRatioSize(savedWidth, fallbackWidth);

        Left = double.IsFinite(settings.OverlayLeft) ? settings.OverlayLeft : 30;
        Top = double.IsFinite(settings.OverlayTop) ? settings.OverlayTop : 80;

        byte alpha = (byte)Math.Round(255 * Math.Clamp(settings.OverlayBackgroundOpacity, 0.10, 1.0));
        var background = new SolidColorBrush(Color.FromArgb(alpha, 7, 15, 22));
        background.Freeze();
        DetailedDesign.Background = background;
        CompactDesign.Background = background;
    }

    public void RefreshSettings(AppSettings settings)
    {
        ApplySettings(settings);
        KeepWindowOnVirtualDesktop();
    }

    private void BeginPlacementMode()
    {
        _originalLeft = Left;
        _originalTop = Top;
        _originalWidth = Width;
        _originalHeight = Height;

        EditChrome.Visibility = Visibility.Visible;
        ApplyWindowInteractionStyle(editMode: true);
        ShowActivated = true;
        Activate();
        Focus();
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || e.LeftButton != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can throw if the mouse button is released between the event
            // and the native move loop. It is harmless; the next drag still works.
        }
    }

    private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!_editMode)
            return;

        double currentWidth = double.IsFinite(ActualWidth) && ActualWidth > 0 ? ActualWidth : Width;
        double currentHeight = double.IsFinite(ActualHeight) && ActualHeight > 0 ? ActualHeight : Height;

        // The handle lives in the bottom-right corner. Whichever mouse movement
        // (horizontal or vertical) is stronger drives the resize; the other axis
        // is calculated from the fixed mode-specific aspect ratio.
        double widthFromHorizontal = currentWidth + e.HorizontalChange;
        double widthFromVertical = (currentHeight + e.VerticalChange) * _aspectRatio;

        double horizontalInfluence = Math.Abs(e.HorizontalChange);
        double verticalInfluence = Math.Abs(e.VerticalChange * _aspectRatio);
        double requestedWidth = horizontalInfluence >= verticalInfluence
            ? widthFromHorizontal
            : widthFromVertical;

        requestedWidth = Math.Clamp(requestedWidth, MinWidth, MaxWidth);
        Width = requestedWidth;
        Height = requestedWidth / _aspectRatio;
    }

    private void SavePlacement_Click(object sender, RoutedEventArgs e)
    {
        if (!_editMode)
            return;

        var latest = _settingsService.Load();
        latest.OverlayLeft = Left;
        latest.OverlayTop = Top;

        if (_mode == "Compact")
        {
            latest.OverlayCompactWidth = Width;
            latest.OverlayCompactHeight = Height;
        }
        else
        {
            latest.OverlayDetailedWidth = Width;
            latest.OverlayDetailedHeight = Height;
        }

        _settingsService.Save(latest);
        PlacementFinished?.Invoke(true);
        Close();
    }

    private void CancelPlacement_Click(object sender, RoutedEventArgs e)
    {
        if (!_editMode)
            return;

        Left = _originalLeft;
        Top = _originalTop;
        Width = _originalWidth;
        Height = _originalHeight;

        PlacementFinished?.Invoke(false);
        Close();
    }

    private void ApplyWindowInteractionStyle(bool editMode)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int style = GetWindowLong(hwnd, GwlExStyle);
        style |= WsExToolWindow;

        if (editMode)
        {
            style &= ~WsExTransparent;
            style &= ~WsExNoActivate;
        }
        else
        {
            // Normal overlay mode never consumes mouse input and never steals focus.
            style |= WsExTransparent;
            style |= WsExNoActivate;
        }

        SetWindowLong(hwnd, GwlExStyle, style);
    }

    private void KeepWindowOnVirtualDesktop()
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        bool completelyOffScreen =
            Left + Width < virtualLeft + 20 ||
            Left > virtualRight - 20 ||
            Top + Height < virtualTop + 20 ||
            Top > virtualBottom - 20;

        if (!completelyOffScreen)
            return;

        Left = virtualLeft + 30;
        Top = virtualTop + 80;
    }


    private void ConfigureAspectRatioLimits(double minimumWidth)
    {
        const double absoluteMaxWidth = 1000.0;
        const double absoluteMaxHeight = 1200.0;

        // Width is the single sizing source of truth. Height limits are derived
        // from it so WPF cannot create a distorted window at either extreme.
        double maximumWidthAllowedByHeight = absoluteMaxHeight * _aspectRatio;
        double maximumWidth = Math.Min(absoluteMaxWidth, maximumWidthAllowedByHeight);

        MinWidth = Math.Min(minimumWidth, maximumWidth);
        MaxWidth = maximumWidth;
        MinHeight = MinWidth / _aspectRatio;
        MaxHeight = MaxWidth / _aspectRatio;
    }

    private void ApplyAspectRatioSize(double savedWidth, double fallbackWidth)
    {
        double width = ClampDimension(savedWidth, fallbackWidth, MinWidth, MaxWidth);
        Width = width;
        Height = width / _aspectRatio;
    }

    private static double ClampDimension(double value, double fallback, double min, double max)
    {
        if (!double.IsFinite(value) || value <= 0)
            value = fallback;

        return Math.Clamp(value, min, max);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
