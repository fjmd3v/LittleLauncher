using LittleLauncher.Services;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace LittleLauncher.Pages;

public partial class AboutPage : Page
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private UpdateService.UpdateCheckResult? _updateResult;

    public AboutPage()
    {
        InitializeComponent();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Checking...";
        UpdateStatusText.Text = "Checking for updates...";

        try
        {
            var result = await UpdateService.CheckForUpdateAsync();
            if (result == null)
            {
                UpdateStatusText.Text = "Unable to check for updates. Check your internet connection.";
            }
            else if (result.UpdateAvailable)
            {
                _updateResult = result;
                UpdateStatusText.Text = result.IsStoreManaged
                    ? $"Version {result.LatestVersion} is available in the Microsoft Store (you have {result.CurrentVersion})"
                    : $"Version {result.LatestVersion} is available (you have {result.CurrentVersion})";
                CheckUpdateButton.Content = !result.IsStoreManaged && string.IsNullOrEmpty(result.MsiDownloadUrl)
                    ? "View Release"
                    : "Download & Install";
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.Click -= CheckForUpdates_Click;
                CheckUpdateButton.Click += DownloadUpdate_Click;
                return;
            }
            else
            {
                UpdateStatusText.Text = $"You're up to date ({result.CurrentVersion})";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Manual update check failed");
            UpdateStatusText.Text = "Update check failed. Try again later.";
        }

        CheckUpdateButton.Content = "Check for Updates";
        CheckUpdateButton.IsEnabled = true;
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateResult == null) return;

        if (_updateResult.IsStoreManaged || !string.IsNullOrEmpty(_updateResult.MsiDownloadUrl))
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content = "Downloading...";

            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                CheckUpdateButton.Content = pct < 100 ? $"Downloading ({pct}%)..." : "Installing...";
            });

            var (success, message) = await UpdateService.DownloadAndInstallAsync(
                _updateResult,
                GetOwnerWindowHandle(),
                progress);

            if (success)
            {
                UpdateStatusText.Text = message;
                await Task.Delay(1000);
                Environment.Exit(0);
            }
            else
            {
                UpdateStatusText.Text = message;
                CheckUpdateButton.Content = "Retry";
                CheckUpdateButton.IsEnabled = true;
            }
        }
        else if (!string.IsNullOrEmpty(_updateResult.ReleaseUrl))
        {
            Process.Start(new ProcessStartInfo(_updateResult.ReleaseUrl) { UseShellExecute = true });
        }
    }

    private static nint GetOwnerWindowHandle()
    {
        Window? owner = SettingsWindow.GetCurrent();
        owner ??= MainWindow.Current;
        return owner == null ? 0 : WindowNative.GetWindowHandle(owner);
    }
}