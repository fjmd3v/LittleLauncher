using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Pages;
using LittleLauncher.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

public partial class FlyoutWindow : Window
{
    private const int ColumnWidth = 175;

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly object BoundsFileLock = new();
    private static readonly ConcurrentDictionary<string, WindowBounds> CachedBounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-launcher flyout window instances (key = Launcher.Id).</summary>
    private static readonly Dictionary<string, FlyoutWindow> _instances = new();

    private DateTime _lastDismissed = DateTime.MinValue;
    private bool _toolWindowStyleApplied;
    private int _lastItemsHash;
    private MainWindow? _owner;
    private IntPtr _hwnd;
    private SUBCLASSPROC? _wndProcDelegate;
    private bool _isShowing;
    private readonly Launcher _launcher;  // The launcher this window displays

    private FlyoutWindow(MainWindow owner, Launcher launcher)
    {
        _owner = owner;
        _launcher = launcher;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Remove titlebar and make borderless, always on top so it
        // renders above the tray overflow popup.
        var presenter = Microsoft.UI.Windowing.OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        GetAppWindow().SetPresenter(presenter);

        RebuildColumnsPanel();

        // Desktop Acrylic blurs whatever is behind the window (including other windows),
        // unlike Mica which only samples the wallpaper.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // OS-level rounded corners (Windows 11) + DWM shadow
        int cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        var margins = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        // Hook WndProc for deactivation detection
        _wndProcDelegate = WndProc;
        SetWindowSubclass(_hwnd, _wndProcDelegate, 2, 0);
        Activated += FlyoutWindow_Activated;

        // Apply saved app theme
        ThemeManager.ApplySavedTheme(this);
    }

    private void FlyoutWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_isShowing) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _lastDismissed = DateTime.UtcNow;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (msg == 0x0100 && wParam == (IntPtr)0x1B) // WM_KEYDOWN + VK_ESCAPE
        {
            HideFlyout();
            _lastDismissed = DateTime.UtcNow;
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    internal static FlyoutWindow? GetCurrent(string? launcherId = null)
    {
        if (launcherId != null)
            return _instances.TryGetValue(launcherId, out var fw) ? fw : null;
        return _instances.Values.FirstOrDefault();
    }

    public static void Toggle(MainWindow owner, int screenX, int screenY, string launcherId)
    {
        if (!_instances.TryGetValue(launcherId, out var instance) || instance == null)
        {
            // Find the launcher
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            if (launcher == null) return;
            instance = new FlyoutWindow(owner, launcher);
            _instances[launcherId] = instance;
        }

        if (instance._hwnd != IntPtr.Zero && IsWindow(instance._hwnd))
        {
            if (GetWindowLong(instance._hwnd, GWL_STYLE) != 0 &&
                (GetWindowLong(instance._hwnd, GWL_STYLE) & WS_VISIBLE) != 0)
            {
                instance.HideFlyout();
                return;
            }
        }

        if ((DateTime.UtcNow - instance._lastDismissed).TotalMilliseconds < 300)
            return;

        instance._owner = owner;
        instance.RebuildItemsIfNeeded();

        // Calculate DPI-aware dimensions
        double dpiScale = GetDpiForWindow(instance._hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        int columnCount = Math.Max(1, instance.ColumnsPanel.Children.Count);
        int flyoutWidthPx = (int)(ColumnWidth * columnCount * dpiScale);
        int flyoutHeightPx = (int)Math.Ceiling(instance.MeasureContentHeight() * dpiScale);

        // Position off-screen first, then show
        instance._isShowing = true;
        var appWindow = instance.GetAppWindow();
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(flyoutWidthPx, flyoutHeightPx));
        ShowWindow(instance._hwnd, SW_SHOWNOACTIVATE);
        SetForegroundWindow(instance._hwnd);
        SetFocus(instance._hwnd);

        if (!instance._toolWindowStyleApplied)
        {
            int exStyle = GetWindowLong(instance._hwnd, GWL_EXSTYLE);
            SetWindowLong(instance._hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            instance._toolWindowStyleApplied = true;
        }

        instance.PositionAt(screenX, screenY);
        instance._isShowing = false;
    }

    public static void DismissIfOpen()
    {
        foreach (var fw in _instances.Values)
            fw.HideFlyout();
    }

    public static void WarmUp(MainWindow owner, IEnumerable<Launcher> launchers)
    {
        foreach (var launcher in launchers)
        {
            if (!_instances.ContainsKey(launcher.Id))
            {
                var fw = new FlyoutWindow(owner, launcher);
                fw._lastItemsHash = ComputeItemsHash(launcher);
                int exStyle = GetWindowLong(fw._hwnd, GWL_EXSTYLE);
                SetWindowLong(fw._hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
                fw._toolWindowStyleApplied = true;
                ShowWindow(fw._hwnd, SW_HIDE);
                _instances[launcher.Id] = fw;
            }
        }
    }

    /// <summary>Destroys the flyout instance for a launcher that has been deleted.</summary>
    public static void DisposeLauncher(string launcherId)
    {
        if (_instances.TryGetValue(launcherId, out var fw))
        {
            fw.Close();
            _instances.Remove(launcherId);
        }
    }

    private void HideFlyout()
    {
        ShowWindow(_hwnd, SW_HIDE);
    }

    private AppWindow GetAppWindow()
    {
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    // ── Content ─────────────────────────────────────────────────────

    private static int ComputeItemsHash(Launcher launcher)
    {
        var items = launcher.Items;
        if (items == null || items.Count == 0) return 0;
        var hash = new HashCode();
        foreach (var item in items)
        {
            HashItem(ref hash, item);
            if (item.IsGroup)
            {
                foreach (var child in item.Children)
                    HashItem(ref hash, child);
            }
        }
        return hash.ToHashCode();
    }

    private static void HashItem(ref HashCode hash, LauncherItem item)
    {
        hash.Add(item.Name);
        hash.Add(item.Path);
        hash.Add(item.IconPath);
        hash.Add(item.IconGlyph);
        hash.Add(item.IsWebsite);
        hash.Add(item.OpenInAppWindow);
        hash.Add(item.AppWindowBrowser);
        hash.Add(item.AppWindowBrowserProfile);
        hash.Add(item.IsGroup);
        hash.Add(item.IsPwa);
        hash.Add(item.IsColumnBreak);
    }

    /// <summary>
    /// Splits the hierarchical item list into per-column display lists.
    /// Items after each <see cref="LauncherItem.IsColumnBreak"/> start a new column.
    /// Groups are flattened (children appended) unless the group is collapsed.
    /// </summary>
    private List<ObservableCollection<LauncherItem>> BuildColumnLists()
    {
        var columns = new List<ObservableCollection<LauncherItem>>();
        var current = new ObservableCollection<LauncherItem>();
        columns.Add(current);

        foreach (var item in _launcher.Items)
        {
            if (item.IsColumnBreak)
            {
                current = new ObservableCollection<LauncherItem>();
                columns.Add(current);
                continue;
            }

            current.Add(item);
            if (item.IsGroup)
            {
                foreach (var child in item.Children)
                    current.Add(child);
            }
        }

        return columns;
    }

    private ListView CreateColumnListView()
    {
        var lv = new ListView
        {
            Width = ColumnWidth,
            Padding = new Thickness(8, 6, 8, 6),
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.None,
            IsTabStop = false,
            TabNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Once,
            ItemTemplateSelector = (DataTemplateSelector)RootGrid.Resources["ItemTemplateSelector"],
            ItemContainerStyleSelector = (StyleSelector)RootGrid.Resources["ItemContainerStyleSelector"],
        };
        ScrollViewer.SetVerticalScrollBarVisibility(lv, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
        lv.ItemClick += ItemsListControl_ItemClick;
        lv.RightTapped += ItemsListControl_RightTapped;
        return lv;
    }

    private void RebuildColumnsPanel()
    {
        ColumnsPanel.Children.Clear();
        foreach (var col in BuildColumnLists())
        {
            var lv = CreateColumnListView();
            lv.ItemsSource = col;
            ColumnsPanel.Children.Add(lv);
        }
    }

    private void RebuildItemsIfNeeded()
    {
        int currentHash = ComputeItemsHash(_launcher);
        if (currentHash != _lastItemsHash)
        {
            _lastItemsHash = currentHash;
            RebuildColumnsPanel();
        }
    }

    /// <summary>
    /// Resets the cached items hash for a specific launcher so the next Toggle forces a full re-bind.
    /// Call after import, sync download, or any bulk item change.
    /// </summary>
    internal static void InvalidateItems(string? launcherId = null)
    {
        if (launcherId != null)
        {
            if (_instances.TryGetValue(launcherId, out var fw))
            {
                fw._lastItemsHash = -1;
                fw.RebuildItemsIfNeeded();
            }
        }
        else
        {
            foreach (var fw in _instances.Values)
            {
                fw._lastItemsHash = -1;
                fw.RebuildItemsIfNeeded();
            }
        }
    }

    /// <summary>Invalidates all launcher flyout instances.</summary>
    internal static void InvalidateAllItems() => InvalidateItems(null);



    // ── Positioning ─────────────────────────────────────────────────

    private void PositionAt(int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        int columnCount = Math.Max(1, ColumnsPanel.Children.Count);
        int flyoutWidth = (int)(ColumnWidth * columnCount * scale);
        int flyoutHeight = (int)Math.Ceiling(MeasureContentHeight() * scale);
        int gap = Math.Max(4, (int)Math.Round(8 * scale));

        int left = screenX - flyoutWidth / 2;
        int top;

        // Detect whether the click is near a taskbar edge or from the tray
        // overflow popup (which floats well inside the work area).
        int edgeThreshold = (int)(16 * scale);
        bool nearBottom = screenY >= workArea.Bottom - edgeThreshold;
        bool nearTop = screenY <= workArea.Top + edgeThreshold;

        if (nearBottom)
        {
            // Taskbar at bottom (common case): position just above taskbar
            top = workArea.Bottom - flyoutHeight - gap;
        }
        else if (nearTop)
        {
            // Taskbar at top: position just below taskbar
            top = workArea.Top + gap;
        }
        else
        {
            // Tray overflow or other mid-screen click: place flyout above the
            // cursor so it doesn't cover the overflow popup the user clicked on.
            top = screenY - flyoutHeight - gap;
        }

        // Clamp within work area
        if (left < workArea.Left) left = workArea.Left;
        if (left + flyoutWidth > workArea.Right) left = workArea.Right - flyoutWidth;
        if (top + flyoutHeight > workArea.Bottom) top = workArea.Bottom - flyoutHeight;
        if (top < workArea.Top) top = workArea.Top;

        SetWindowPos(_hwnd, 0, left, top, flyoutWidth, flyoutHeight,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void ItemsListControl_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LauncherItem item) return;

        if (item.IsGroup) return;

        HideFlyout();
        _lastDismissed = DateTime.UtcNow;

        try
        {
            if (item.IsWebsite || item.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                               || item.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                LaunchWebsite(item);
            }
            else if (item.IsPwa)
            {
                // PWA items store a shell:AppsFolder AUMID in Path
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"shell:AppsFolder\\{item.Path}",
                    UseShellExecute = false
                });
            }
            else if (item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                // Store / packaged apps: ShellExecuteEx understands shell:AppsFolder\{AUMID}
                // and forwards lpParameters (Arguments) to the activated package.
                Process.Start(new ProcessStartInfo(item.Path)
                {
                    UseShellExecute = true,
                    Arguments = item.Arguments ?? ""
                });
            }
            else
            {
                var args = item.Arguments ?? "";
                var path = item.Path;
                ProcessStartInfo psi;
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    // Shell resolves .lnk; user-supplied Arguments are intentionally ignored
                    psi = new ProcessStartInfo(path) { UseShellExecute = true };
                }
                else if (path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                      || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    psi = new ProcessStartInfo("cmd.exe")
                    {
                        Arguments = $"/c \"{path}\" {args}",
                        UseShellExecute = false
                    };
                }
                else
                {
                    // Use CreateProcess directly so arguments are always forwarded,
                    // including for MSIX App Execution Aliases (e.g. wt.exe).
                    psi = new ProcessStartInfo(path)
                    {
                        Arguments = args,
                        UseShellExecute = false
                    };
                }
                Process.Start(psi);
            }
            Logger.Info($"Launched from flyout: {item.Name} ({item.Path})");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to launch from flyout: {item.Name} ({item.Path})");
        }
    }

    private void ItemsListControl_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement fe) return;
        var item = fe.DataContext as LauncherItem;
        if (item == null || item.IsGroup) return;

        var lv = sender as ListView ?? ItemsListControl_GetAny();
        var flyout = new MenuFlyout();
        var editItem = new MenuFlyoutItem { Text = "Edit" };
        editItem.Click += (_, _) => EditItem(item);
        flyout.Items.Add(editItem);

        if (lv != null)
            flyout.ShowAt(lv, e.GetPosition(lv));
    }

    private ListView? ItemsListControl_GetAny()
    {
        return ColumnsPanel.Children.OfType<ListView>().FirstOrDefault();
    }

    private static void LaunchWebsite(LauncherItem item)
    {
        if (!item.OpenInAppWindow)
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            return;
        }

        if (TryLaunchInAppWindow(item.Path, item.AppWindowBrowser, item.AppWindowBrowserProfile))
            return;

        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    private enum BrowserEngine { Chromium, Gecko }

    private static BrowserEngine DetectEngine(string exePath)
    {
        string? dir = Path.GetDirectoryName(exePath);
        if (dir != null && (File.Exists(Path.Combine(dir, "chrome.dll")) ||
                            File.Exists(Path.Combine(dir, "msedge.dll"))))
            return BrowserEngine.Chromium;

        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (name == "firefox" || name == "zen" || name == "waterfox" ||
            name == "librewolf" || name == "floorp" || name == "mercury" || name == "firedragon")
            return BrowserEngine.Gecko;

        return BrowserEngine.Chromium;
    }

    private static bool TryLaunchInAppWindow(string url, string browserPath, string browserProfile)
    {
        string profileId = GetAppWindowProfileId(url);
        string browserExe = ResolveBrowserExe(browserPath);
        if (browserExe == "") return false;

        var engine = DetectEngine(browserExe);
        var existingWindows = GetBrowserWindows(engine);

        try
        {
            string args = engine == BrowserEngine.Gecko
                ? BuildGeckoArgs(url, profileId)
                : BuildChromiumArgs(url, browserProfile, profileId);

            Process.Start(new ProcessStartInfo { FileName = browserExe, Arguments = args, UseShellExecute = false });
            _ = RestoreAndTrackWindowBoundsAsync(existingWindows, profileId, engine);
            return true;
        }
        catch { return false; }
    }

    private static string BuildChromiumArgs(string url, string browserProfile, string profileId)
    {
        string args = $"--app=\"{url}\"";
        if (string.IsNullOrEmpty(browserProfile))
        {
            string appProfileDir = GetAppWindowProfileDirectory(profileId);
            Directory.CreateDirectory(appProfileDir);
            args += $" --user-data-dir=\"{appProfileDir}\"";
        }
        else if (browserProfile != "__default__")
        {
            args += $" --profile-directory=\"{browserProfile}\"";
        }
        return args;
    }

    private static string BuildGeckoArgs(string url, string profileId)
    {
        string appProfileDir = GetAppWindowProfileDirectory(profileId);
        Directory.CreateDirectory(appProfileDir);
        EnsureGeckoAppWindowProfile(appProfileDir);
        return $"--new-window \"{url}\" --profile \"{appProfileDir}\" --no-remote";
    }

    private static void EnsureGeckoAppWindowProfile(string profileDir)
    {
        string chromeDir = Path.Combine(profileDir, "chrome");
        Directory.CreateDirectory(chromeDir);

        string userChromePath = Path.Combine(chromeDir, "userChrome.css");
        if (!File.Exists(userChromePath))
        {
            File.WriteAllText(userChromePath,
                "@namespace url(\"http://www.mozilla.org/keymaster/gatekeeper/there.is.only.xul\"); #navigator-toolbox { visibility: collapse !important; }");
        }

        string userJsPath = Path.Combine(profileDir, "user.js");
        if (!File.Exists(userJsPath))
        {
            File.WriteAllText(userJsPath,
                "user_pref(\"toolkit.legacyUserProfileCustomizations.stylesheets\", true);\n" +
                "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
                "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n" +
                "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);\n");
        }
    }

    private static string ResolveBrowserExe(string browserPath)
    {
        if (!string.IsNullOrEmpty(browserPath))
            return File.Exists(browserPath) ? browserPath : "";
        return GetDefaultBrowserExePath() ?? "";
    }

    private static string? GetDefaultBrowserExePath()
    {
        try
        {
            int size = 512;
            var sb = new StringBuilder(size);
            int hr = AssocQueryString(ASSOCF_NONE, ASSOCSTR_EXECUTABLE, "https", "open", sb, ref size);
            if (hr == 0)
            {
                string exePath = sb.ToString();
                if (File.Exists(exePath)) return exePath;
            }
        }
        catch { }
        return null;
    }

    private static string GetAppWindowProfileId(string url)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string GetAppWindowProfileDirectory(string profileId)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LittleLauncher", "AppWindowProfiles", profileId);
    }

    private static string GetBoundsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LittleLauncher", "edge-window-bounds.json");
    }

    private static async Task RestoreAndTrackWindowBoundsAsync(HashSet<IntPtr> existingWindows, string profileId, BrowserEngine engine)
    {
        IntPtr hwnd = await WaitForNewBrowserWindowAsync(existingWindows, engine, TimeSpan.FromSeconds(10));
        if (hwnd == IntPtr.Zero) return;

        if (TryGetSavedBounds(profileId, out var savedBounds))
        {
            SetWindowPos(hwnd, 0, savedBounds.Left, savedBounds.Top, savedBounds.Width, savedBounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            if (savedBounds.IsMaximized) ShowWindow(hwnd, SW_MAXIMIZE);
        }

        InstallBoundsTrackingHooks(hwnd, profileId);
    }

    private static readonly HashSet<WinEventProc> ActiveHookDelegates = new();

    private static void InstallBoundsTrackingHooks(IntPtr hwnd, string profileId)
    {
        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);
        WindowBounds? lastBounds = null;
        IntPtr hookLocation = IntPtr.Zero;
        IntPtr hookDestroy = IntPtr.Zero;
        WinEventProc? handler = null;

        handler = (hHook, eventType, eventHwnd, idObject, idChild, eventThread, time) =>
        {
            if (eventHwnd != hwnd || idObject != OBJID_WINDOW) return;
            if (eventType == EVENT_OBJECT_LOCATIONCHANGE)
            {
                if (GetWindowRect(hwnd, out RECT rect))
                {
                    bool maximized = IsZoomed(hwnd);
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w >= 320 && h >= 240)
                        lastBounds = new WindowBounds(rect.Left, rect.Top, w, h, maximized);
                }
            }
            else if (eventType == EVENT_OBJECT_DESTROY)
            {
                if (lastBounds is not null) SaveBounds(profileId, lastBounds);
                if (hookLocation != IntPtr.Zero) UnhookWinEvent(hookLocation);
                if (hookDestroy != IntPtr.Zero) UnhookWinEvent(hookDestroy);
                lock (ActiveHookDelegates) ActiveHookDelegates.Remove(handler!);
            }
        };

        lock (ActiveHookDelegates) ActiveHookDelegates.Add(handler);

        hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, handler, processId, threadId, WINEVENT_OUTOFCONTEXT);
        hookDestroy = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY,
            IntPtr.Zero, handler, processId, threadId, WINEVENT_OUTOFCONTEXT);
    }

    private static async Task<IntPtr> WaitForNewBrowserWindowAsync(HashSet<IntPtr> existingWindows, BrowserEngine engine, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var currentWindows = GetBrowserWindows(engine);
            foreach (var hwnd in currentWindows)
            {
                if (existingWindows.Contains(hwnd)) continue;
                if (!GetWindowRect(hwnd, out RECT rect)) continue;
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width >= 200 && height >= 120) return hwnd;
            }
            await Task.Delay(200);
        }
        return IntPtr.Zero;
    }

    private static readonly string[] ChromiumWindowClasses = { "Chrome_WidgetWin_1" };
    private static readonly string[] GeckoWindowClasses = { "MozillaWindowClass", "MozillaDialogClass" };

    private static HashSet<IntPtr> GetBrowserWindows(BrowserEngine engine)
    {
        var windowClasses = engine == BrowserEngine.Gecko ? GeckoWindowClasses : ChromiumWindowClasses;
        var windows = new HashSet<IntPtr>();
        var className = new StringBuilder(256);
        EnumWindows((hWnd, _) =>
        {
            className.Clear();
            GetClassName(hWnd, className, className.Capacity);
            string cls = className.ToString();
            foreach (string target in windowClasses)
            {
                if (cls == target) { windows.Add(hWnd); break; }
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool TryGetSavedBounds(string profileId, out WindowBounds bounds)
    {
        bounds = default!;
        lock (BoundsFileLock)
        {
            if (CachedBounds.TryGetValue(profileId, out var cachedBounds)) { bounds = cachedBounds; return true; }
            var all = LoadAllBounds();
            foreach (var kv in all) CachedBounds[kv.Key] = kv.Value;
            if (CachedBounds.TryGetValue(profileId, out var loadedBounds)) { bounds = loadedBounds; return true; }
            return false;
        }
    }

    private static void SaveBounds(string profileId, WindowBounds bounds)
    {
        lock (BoundsFileLock)
        {
            CachedBounds[profileId] = bounds;
            var all = LoadAllBounds();
            all[profileId] = bounds;
            string filePath = GetBoundsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            string json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
    }

    private static Dictionary<string, WindowBounds> LoadAllBounds()
    {
        string filePath = GetBoundsFilePath();
        if (!File.Exists(filePath)) return new Dictionary<string, WindowBounds>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<Dictionary<string, WindowBounds>>(json) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private sealed record WindowBounds(int Left, int Top, int Width, int Height, bool IsMaximized = false);

    private void EditItem(LauncherItem item)
    {
        HideFlyout();
        _lastDismissed = DateTime.UtcNow;
        if (_owner != null)
        {
            LauncherItemsPage.TargetLauncher = _launcher;
            SettingsWindow.NavigateToEditItem(item, _owner);
        }
    }

    private void ContextSettingsItem_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _lastDismissed = DateTime.UtcNow;
        if (_owner != null)
            SettingsWindow.ShowInstance(_owner);
    }

    private double _lastMeasuredHeight = 80;

    private double MeasureContentHeight()
    {
        // Calculate height arithmetically instead of calling UpdateLayout()/Measure()
        // on a potentially hidden window. Forcing a XAML layout pass on a window hidden
        // via ShowWindow(SW_HIDE) while another WinUI 3 window is active causes a fatal
        // ExecutionEngineException in Microsoft.WinUI.dll.
        //
        // Each ListViewItem container: MinHeight=0, Padding="8,6" → 12px vertical padding.
        // Regular item content: Icon 20px tall → total ~32px
        // Group header content: FontSize=11 (~15px) + Margin top 6 → total ~33px
        // Heading content:      FontSize=11 (~15px) + Margin top 4 → total ~31px
        const double itemHeight = 32;
        const double groupHeight = 33;
        const double listPadding = 12;     // ListView Padding="8,6,8,6" → 6+6

        var items = _launcher.Items;
        if (items == null) return _lastMeasuredHeight;

        // Compute the height of each column and take the tallest.
        double maxColumnHeight = 0;
        double currentColumnHeight = listPadding;

        foreach (var item in items)
        {
            if (item.IsColumnBreak)
            {
                maxColumnHeight = Math.Max(maxColumnHeight, currentColumnHeight);
                currentColumnHeight = listPadding;
                continue;
            }

            if (item.IsGroup)
                currentColumnHeight += groupHeight;
            else
                currentColumnHeight += itemHeight;

            if (item.IsGroup)
            {
                foreach (var child in item.Children)
                {
                    currentColumnHeight += itemHeight;
                }
            }
        }

        maxColumnHeight = Math.Max(maxColumnHeight, currentColumnHeight);

        // Add a small buffer to cover accumulated sub-pixel font-height rounding.
        // Clamp to the available work-area height so the flyout never exceeds the screen.
        double maxContentHeight = GetWorkAreaHeightDips() - 16; // 16 = gap from taskbar edges
        _lastMeasuredHeight = Math.Clamp(maxColumnHeight + 2, 80, maxContentHeight);
        return _lastMeasuredHeight;
    }

    private double GetWorkAreaHeightDips()
    {
        var pt = new POINT();
        GetCursorPos(out pt);
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiY, out _);
        double scale = dpiY / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        int workAreaHeightPx = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
        return workAreaHeightPx / scale;
    }

}