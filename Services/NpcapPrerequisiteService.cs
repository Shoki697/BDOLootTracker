using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace BDOLootTracker.Services;

public static class NpcapPrerequisiteService
{
    private const string NpcapDownloadUrl = "https://npcap.com/#download";
    private const string NpcapParametersKey = @"SYSTEM\CurrentControlSet\Services\npcap\Parameters";
    private const string NpcapServiceKey = @"SYSTEM\CurrentControlSet\Services\npcap";

    /// <summary>
    /// Detects Npcap using the registry location documented by Npcap and,
    /// as a fallback, the standard System32\\Npcap DLL directory.
    /// </summary>
    public static bool IsInstalled()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var parameters = hklm.OpenSubKey(NpcapParametersKey, writable: false);
            if (parameters != null)
                return true;

            using var service = hklm.OpenSubKey(NpcapServiceKey, writable: false);
            if (service != null)
                return true;
        }
        catch
        {
            // Registry access should normally succeed. If it does not, fall
            // back to checking the documented Npcap DLL installation path.
        }

        try
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string npcapDirectory = Path.Combine(systemDirectory, "Npcap");

            return File.Exists(Path.Combine(npcapDirectory, "wpcap.dll")) ||
                   File.Exists(Path.Combine(npcapDirectory, "Packet.dll"));
        }
        catch
        {
            return false;
        }
    }

    public static string? GetInstalledVersion()
    {
        try
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string npcapDirectory = Path.Combine(systemDirectory, "Npcap");

            foreach (string fileName in new[] { "wpcap.dll", "Packet.dll" })
            {
                string path = Path.Combine(npcapDirectory, fileName);
                if (!File.Exists(path))
                    continue;

                string? version = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (!string.IsNullOrWhiteSpace(version))
                    return version.Trim();
            }
        }
        catch
        {
            // Version text is diagnostic only. Installation detection still works.
        }

        return null;
    }

    /// <summary>
    /// Shows a first-run / Start-button prerequisite warning and optionally
    /// opens the official Npcap download page. Npcap is not bundled with the
    /// application.
    /// </summary>
    public static bool PromptIfMissing()
    {
        if (IsInstalled())
            return true;

        var result = MessageBox.Show(
            "Npcap was not detected on this computer.\n\n" +
            "BDO Loot Tracker needs Npcap to capture Black Desert Online network packets. " +
            "The application can be opened without it, but loot tracking cannot be started.\n\n" +
            "Open the official Npcap download page now?",
            "Npcap required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
            OpenNpcapDownloadPage();

        return false;
    }

    private static void OpenNpcapDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = NpcapDownloadUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open the Npcap download page.\n\n{ex.Message}\n\n{NpcapDownloadUrl}",
                "BDO Loot Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
