using System.Windows;
using BDOLootTracker.Models;

namespace BDOLootTracker.Views;

public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow(string version, ChangelogEntry entry)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrWhiteSpace(entry.Title) ? "What's New" : entry.Title;
        VersionText.Text = $"BDO Loot Tracker v{version.TrimStart('v', 'V')}";
        ChangesList.ItemsSource = entry.Changes;
    }

    private void GotIt_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
