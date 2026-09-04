using System.Windows;
using System.Windows.Input;

namespace BDOLootTracker.Views;

public partial class UpdateAvailableWindow : Window
{
    public bool UpdateRequested { get; private set; }

    public UpdateAvailableWindow(string currentVersion, string newVersion, string releaseNotes)
    {
        InitializeComponent();
        CurrentVersionText.Text = $"v{currentVersion}";
        NewVersionText.Text = $"v{newVersion}";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(releaseNotes)
            ? "No detailed release notes were provided for this build."
            : releaseNotes;
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateRequested = true;
        DialogResult = true;
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        UpdateRequested = false;
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
