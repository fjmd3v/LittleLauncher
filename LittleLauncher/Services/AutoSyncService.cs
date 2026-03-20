using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;

namespace LittleLauncher.Services;

/// <summary>
/// Manages automatic SFTP sync of launcher items.
/// Handles: startup download, debounced upload on item changes, and periodic upload.
/// All triggers are gated by the SftpAutoSync toggle.
/// </summary>
public static class AutoSyncService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static System.Threading.Timer? _periodicTimer;
    private static System.Threading.Timer? _debounceTimer;
    private static bool _running;

    /// <summary>
    /// Start periodic sync timer. Call once at app startup (after startup sync completes).
    /// </summary>
    public static void Start()
    {
        if (_running) return;
        _running = true;
        RestartPeriodicTimer();
    }

    /// <summary>
    /// Stop all sync timers. Call at app shutdown.
    /// </summary>
    public static void Stop()
    {
        _running = false;
        _periodicTimer?.Dispose();
        _periodicTimer = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    /// <summary>
    /// Restart the periodic timer (e.g. when interval setting changes).
    /// </summary>
    public static void RestartPeriodicTimer()
    {
        _periodicTimer?.Dispose();
        _periodicTimer = null;

        if (!_running || !SettingsManager.Current.SftpAutoSync)
            return;

        int minutes = SettingsManager.Current.SftpAutoSyncInterval;
        if (minutes <= 0) minutes = 5;
        var interval = TimeSpan.FromMinutes(minutes);

        _periodicTimer = new System.Threading.Timer(
            _ => _ = DownloadSilentAsync("periodic"),
            null,
            interval,
            interval);
    }

    /// <summary>
    /// Notify that launcher items changed. Triggers a debounced upload (3 seconds).
    /// </summary>
    public static void NotifyItemsChanged()
    {
        if (!SettingsManager.Current.SftpAutoSync
            || string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
            return;

        // Reset the debounce timer — upload 3 seconds after last change
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(
            _ => _ = UploadSilentAsync("debounced item change"),
            null,
            TimeSpan.FromSeconds(3),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Run the startup sync: download launcher items from the server, and sync all shared groups.
    /// </summary>
    public static async Task SyncOnStartupAsync()
    {
        // Main SFTP sync (if configured)
        if (SettingsManager.Current.SftpAutoSync
            && !string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
        {
            try
            {
                var (success, message) = await SftpSyncService.DownloadLauncherItemsAsync();
                if (success)
                {
                    Logger.Info("Auto-sync startup: downloaded launcher items");
                    Windows.FlyoutWindow.InvalidateItems();
                }
                else
                    Logger.Warn($"Auto-sync startup skipped: {message}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Auto-sync startup failed");
            }
        }

        // Shared group sync (always runs if there are any shared groups)
        var sources = SettingsManager.Current.SharedGroupSources;
        if (sources.Count > 0)
        {
            try
            {
                // Download subscribed groups first, then push owned groups
                await SharedGroupSyncService.SyncAllIncomingAsync();
                await SharedGroupSyncService.SyncAllOutgoingAsync();
                if (sources.Any(s => !s.IsOwner))
                    Windows.FlyoutWindow.InvalidateItems();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Shared group startup sync failed");
            }
        }
    }

    private static async Task UploadSilentAsync(string trigger)
    {
        if (!SettingsManager.Current.SftpAutoSync
            || string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
            return;

        try
        {
            var (success, message) = await SftpSyncService.UploadLauncherItemsAsync();
            if (success)
                Logger.Info($"Auto-sync ({trigger}): uploaded launcher items");
            else
                Logger.Warn($"Auto-sync ({trigger}) skipped: {message}");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Auto-sync ({trigger}) failed");
        }
    }

    private static async Task DownloadSilentAsync(string trigger)
    {
        if (!SettingsManager.Current.SftpAutoSync
            || string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
            return;

        try
        {
            var (success, message) = await SftpSyncService.DownloadLauncherItemsAsync();
            if (success)
            {
                Logger.Info($"Auto-sync ({trigger}): downloaded launcher items");
                Windows.FlyoutWindow.InvalidateItems();
            }
            else
                Logger.Warn($"Auto-sync ({trigger}) skipped: {message}");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Auto-sync ({trigger}) failed");
        }

        // Also sync incoming shared groups on the periodic timer
        await SyncSharedGroupsSilentAsync(trigger);
    }

    private static async Task SyncSharedGroupsSilentAsync(string trigger)
    {
        var sources = SettingsManager.Current.SharedGroupSources;
        if (sources.Count == 0) return;

        try
        {
            bool hadIncoming = sources.Any(s => !s.IsOwner);
            await SharedGroupSyncService.SyncAllIncomingAsync();
            if (hadIncoming)
                Windows.FlyoutWindow.InvalidateItems();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Shared group sync ({trigger}) failed");
        }
    }
}
