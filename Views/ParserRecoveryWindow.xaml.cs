using System.Windows;

namespace BDOLootTracker.Views;

public enum ParserRecoveryAction
{
    Later,
    Diagnostics,
    AutoRepair
}

public partial class ParserRecoveryWindow : Window
{
    public ParserRecoveryAction SelectedAction { get; private set; } = ParserRecoveryAction.Later;

    public ParserRecoveryWindow(string profileVersion, string message)
    {
        InitializeComponent();
        ProfileText.Text = string.IsNullOrWhiteSpace(profileVersion)
            ? "Active profile: unknown"
            : $"Active profile: {profileVersion}";
        MessageText.Text = message;
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = ParserRecoveryAction.Later;
        DialogResult = true;
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = ParserRecoveryAction.Diagnostics;
        DialogResult = true;
    }

    private void AutoRepair_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = ParserRecoveryAction.AutoRepair;
        DialogResult = true;
    }
}
