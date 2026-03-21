using LittleLauncher.Classes.Settings;
using LittleLauncher.Services;
using LittleLauncher.ViewModels;
using NLog;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LittleLauncher.Pages;

public partial class HomePage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal UserSettings Settings => SettingsManager.Current;

    private UpdateService.UpdateCheckResult? _updateResult;

    public HomePage()
    {
        InitializeComponent();
        LoadAppIcon();
        VersionTextBlock.Text = SettingsManager.Current.LastKnownVersion;
        BuildTypeTextBlock.Text = GetBuildTypeLabel();
        _ = CheckForUpdateAsync();
    }

    private static string GetBuildTypeLabel()
    {
#if DEBUG
        return "(Debug)";
#else
        return MainWindow.IsPackaged ? "(MSIX)" : "(WiX)";
#endif
    }

    private void LoadAppIcon()
    {
        // Prefer the source PNG/image for crisp rendering — BitmapImage handles PNG
        // much better than ICO on high-DPI (WIC's ICO decoder may pick a low-res frame).
        string? source = ResolveAppIconSource();
        if (source == null) return;

        var bmp = new BitmapImage();
        bmp.DecodePixelWidth = 256;
        bmp.DecodePixelHeight = 256;
        bmp.UriSource = new Uri(source);
        AppIcon.Source = bmp;
    }

    /// <summary>
    /// Returns the best image source path for the current tray icon mode.
    /// Prefers native PNG/image files over the generated ICO.
    /// </summary>
    private static string? ResolveAppIconSource()
    {
        var firstLauncher = SettingsManager.Current.Launchers.Count > 0
            ? SettingsManager.Current.Launchers[0] : null;
        int mode = firstLauncher?.TrayIconMode ?? 0;

        // Preset color icons (modes 0–5): load the source PNG directly
        string[] presetNames = ["Blue", "Green", "Teal", "Red", "Orange", "Purple"];
        if (mode >= 0 && mode < presetNames.Length)
        {
            string png = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", $"{presetNames[mode]}.png");
            if (File.Exists(png)) return png;
        }

        // Custom icon (mode 12): load the user's original file
        if (mode == 12)
        {
            string custom = firstLauncher?.CustomTrayIconPath ?? "";
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
                return custom;
        }

        // Glyph presets and fallback: use the generated ICO
        string ico = Path.Combine(
            MainWindow.GetPhysicalAppDataDir(), "app-icon.ico");
        return File.Exists(ico) ? ico : null;
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            // Use the cached startup result if available; otherwise check now.
            var result = UpdateService.LatestResult ?? await UpdateService.CheckForUpdateAsync();
            if (result is { UpdateAvailable: true })
            {
                _updateResult = result;
                UpdateInfoBar.Message = $"A new version ({result.LatestVersion}) is available. You are running {result.CurrentVersion}.";
                UpdateInfoBar.IsOpen = true;

                if (string.IsNullOrEmpty(result.MsiDownloadUrl))
                {
                    UpdateActionButton.Content = "View Release";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Update check failed on HomePage");
        }
    }

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_updateResult == null) return;

        if (!string.IsNullOrEmpty(_updateResult.MsiDownloadUrl))
        {
            UpdateActionButton.IsEnabled = false;
            UpdateActionButton.Content = "Downloading...";

            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                UpdateActionButton.Content = pct < 100 ? $"Downloading ({pct}%)..." : "Installing...";
            });

            var (success, message) = await UpdateService.DownloadAndInstallAsync(
                _updateResult.MsiDownloadUrl, progress);

            if (success)
            {
                UpdateInfoBar.Message = "Installer will launch after the app closes.";
                UpdateInfoBar.Severity = InfoBarSeverity.Success;
                await Task.Delay(1000);
                Environment.Exit(0);
            }
            else
            {
                UpdateActionButton.Content = "Download & Install";
                UpdateActionButton.IsEnabled = true;
                UpdateInfoBar.Message = message;
                UpdateInfoBar.Severity = InfoBarSeverity.Error;
            }
        }
        else if (!string.IsNullOrEmpty(_updateResult.ReleaseUrl))
        {
            Process.Start(new ProcessStartInfo(_updateResult.ReleaseUrl) { UseShellExecute = true });
        }
    }

    private void LauncherItems_Click(object sender, PointerRoutedEventArgs e)
    {
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(LaunchersPage));
    }

    private void SyncSettings_Click(object sender, PointerRoutedEventArgs e)
    {
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(SyncPage));
    }

    private void DashboardCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
        }
    }

    private void DashboardCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }
    }

    private void TrayIconSwitch_Toggled(object sender, RoutedEventArgs e) { }
}