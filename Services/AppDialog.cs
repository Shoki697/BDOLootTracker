using System.Windows;
using BDOLootTracker.Views;

namespace BDOLootTracker.Services;

public static class AppDialog
{
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        // Velopack's very early first-run hook can execute before WPF has created
        // Application.Current and loaded the tracker resource dictionary. In that
        // one bootstrap-only case a native fallback is safer than constructing a
        // themed window without its resources. All normal in-app dialogs use the
        // custom tracker window below.
        if (Application.Current == null)
            return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon);

        Window? owner = FindOwner();
        var dialog = new AppDialogWindow(messageBoxText, caption, button, icon);
        if (owner != null && owner.IsVisible)
            dialog.Owner = owner;

        dialog.ShowDialog();
        return dialog.Result;
    }

    public static MessageBoxResult Show(string messageBoxText, string caption)
        => Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    private static Window? FindOwner()
    {
        if (Application.Current == null)
            return null;

        return Application.Current.Windows
            .OfType<Window>()
            .Where(x => x.IsVisible && x is not AppDialogWindow)
            .OrderByDescending(x => x.IsActive)
            .FirstOrDefault();
    }
}
