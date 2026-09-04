using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BDOLootTracker.Views;

public partial class AppDialogWindow : Window
{
    private readonly MessageBoxButton _buttons;
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public AppDialogWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        _buttons = buttons;
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "BDO Loot Tracker" : title;
        MessageText.Text = message ?? string.Empty;
        ConfigureIcon(image);
        ConfigureButtons(buttons);
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        switch (image)
        {
            case MessageBoxImage.Error:
                IconText.Text = "×";
                IconText.Foreground = (Brush)FindResource("Red");
                break;
            case MessageBoxImage.Warning:
                IconText.Text = "!";
                IconText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                break;
            case MessageBoxImage.Question:
                IconText.Text = "?";
                IconText.Foreground = (Brush)FindResource("Accent");
                break;
            default:
                IconText.Text = "i";
                IconText.Foreground = (Brush)FindResource("Accent");
                break;
        }
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                OkButton.Visibility = Visibility.Visible;
                OkButton.IsDefault = true;
                break;
            case MessageBoxButton.OKCancel:
                OkButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                OkButton.IsDefault = true;
                CancelButton.IsCancel = true;
                break;
            case MessageBoxButton.YesNo:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                YesButton.IsDefault = true;
                break;
            case MessageBoxButton.YesNoCancel:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                YesButton.IsDefault = true;
                CancelButton.IsCancel = true;
                break;
        }
    }

    private void Complete(MessageBoxResult result)
    {
        Result = result;
        DialogResult = true;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Complete(MessageBoxResult.OK);
    private void YesButton_Click(object sender, RoutedEventArgs e) => Complete(MessageBoxResult.Yes);
    private void NoButton_Click(object sender, RoutedEventArgs e) => Complete(MessageBoxResult.No);
    private void CancelButton_Click(object sender, RoutedEventArgs e) => Complete(MessageBoxResult.Cancel);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Result = _buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.None
        };
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Result == MessageBoxResult.None)
        {
            Result = _buttons switch
            {
                MessageBoxButton.OK => MessageBoxResult.OK,
                MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
                _ => MessageBoxResult.None
            };
        }

        base.OnClosing(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
