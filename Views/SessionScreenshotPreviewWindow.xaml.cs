using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using BDOLootTracker.Services;
using Microsoft.Win32;

namespace BDOLootTracker.Views;

public partial class SessionScreenshotPreviewWindow : Window
{
    private readonly BitmapSource _bitmap;
    private readonly string _suggestedFileName;

    public SessionScreenshotPreviewWindow(BitmapSource bitmap, string suggestedFileName)
    {
        InitializeComponent();
        _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        _suggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName)
            ? "BDOLootTracker_Session.png"
            : suggestedFileName;

        PreviewImage.Source = _bitmap;
        ImageInfoText.Text = $"{_bitmap.PixelWidth:N0} × {_bitmap.PixelHeight:N0} PNG";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetImage(_bitmap);
            StatusText.Text = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Session Screenshot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Session Screenshot",
                Filter = "PNG image (*.png)|*.png",
                DefaultExt = ".png",
                AddExtension = true,
                FileName = _suggestedFileName
            };

            if (dialog.ShowDialog(this) != true)
                return;

            SavePng(_bitmap, dialog.FileName);
            StatusText.Text = $"Saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            AppDialog.Show(ex.Message, "Session Screenshot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
