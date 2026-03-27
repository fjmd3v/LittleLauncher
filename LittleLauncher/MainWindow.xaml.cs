using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Windows;
using LittleLauncher.Services;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.Drawing.Imaging;
using System.IO;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher;

/// <summary>
/// MainWindow — the invisible host window for the Little Launcher (WinUI 3).
///
/// Architecture notes:
///   - This window is never actually displayed.
///   - Its sole purpose is to own the tray icon.
///   - A Mutex ("LittleLauncher") prevents multiple instances.
///   - A registered window message lets a second instance or LauncherShortcut
///     signal the first to show the flyout via PostMessage.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>The running MainWindow instance. Set in the constructor; null before construction.</summary>
    public static new MainWindow? Current { get; private set; }

    /// <summary>
    /// True when running as an MSIX-packaged app (has package identity).
    /// Cached once at startup — never changes during the process lifetime.
    /// </summary>
    internal static bool IsPackaged { get; } = DetectPackagedContext();

    private static bool DetectPackagedContext()
    {
        try
        {
            // Accessing Package.Current throws InvalidOperationException in unpackaged apps.
            _ = global::Windows.ApplicationModel.Package.Current.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the physical (non-VFS) path for the app's data directory.
    /// In MSIX, writes to %AppData%\LittleLauncher are VFS-redirected to a
    /// package-specific folder that external processes (shell, companion exe)
    /// cannot see. This method returns the real redirect target path so that
    /// external references (.lnk IconLocation, etc.) point to existing files.
    /// </summary>
    internal static string GetPhysicalAppDataDir()
    {
        if (IsPackaged)
        {
            return Path.Combine(
                global::Windows.Storage.ApplicationData.Current.RoamingFolder.Path,
                "LittleLauncher");
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher");
    }

    /// <summary>Holds the Win32 HICON state for one launcher's tray icon.</summary>
    private sealed class TrayIconEntry
    {
        public uint Uid;
        public System.Drawing.Icon? Icon;
        public bool IsAdded; // false when NIconHide=true (NIM_DELETE called)
    }

    private static readonly Mutex Singleton = new(true, "LittleLauncher");

    /// <summary>Per-launcher registered window message IDs (key = Launcher.Id).</summary>
    private readonly Dictionary<string, int> _wmShowFlyoutPerLauncher = new();
    private static int _wmShowSettings;
    private int _wmTrayCallback;
    private IntPtr _hwnd;
    private SUBCLASSPROC? _wndProcDelegate;

    /// <summary>Per-launcher native tray icons (key = Launcher.Id).</summary>
    private readonly Dictionary<string, TrayIconEntry> _trayIcons = new();
    private uint _nextIconId = 1;
    private readonly global::Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private bool _lastDarkTheme;

    public MainWindow()
    {
        // ── Singleton check ─────────────────────────────────────────
        if (!Singleton.WaitOne(TimeSpan.Zero, true))
        {
            // Another instance is running — signal it and exit.
            IntPtr target = FindWindow(null, "Little Launcher Host");

            string[] args = Environment.GetCommandLineArgs();
            bool isSilent = args.Length > 1 &&
                args[1].Equals("--silent", StringComparison.OrdinalIgnoreCase);

            if (!isSilent)
            {
                // Default: show settings (Start Menu, double-click exe, etc.).
                // The companion exe handles flyout signaling itself via
                // PostMessage and never launches a second LittleLauncher.exe
                // when the app is already running.
                int msg = RegisterWindowMessage("LittleLauncher_ShowSettings");
                if (target != IntPtr.Zero && msg != 0)
                    PostMessage(target, msg, IntPtr.Zero, IntPtr.Zero);
            }
            // --silent second instance: just exit quietly

            Environment.Exit(0);
        }

        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        Current = this;

        Logger.Info("Starting Little Launcher MainWindow");

        // Register the window message for cross-process flyout signaling.
        // Per-launcher show-flyout messages are registered in CreateTrayIconForLauncher().
        _wmShowSettings = RegisterWindowMessage("LittleLauncher_ShowSettings");

        // Also register the legacy (no-launcher) message for backward compat with old pinned shortcuts,
        // and store it under an empty key so WndProc can route it to the first launcher.
        int legacyMsg = RegisterWindowMessage("LittleLauncher_ShowFlyout");
        _wmShowFlyoutPerLauncher[string.Empty] = legacyMsg;

        // Hide from Alt-Tab and make invisible
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        // Move off-screen
        var appWindow = GetAppWindow();
        appWindow.Move(new global::Windows.Graphics.PointInt32(-9999, -9999));
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(1, 1));

        // Prevent WinUI 3 from closing the host window (and terminating the app)
        // when other windows close. Exit only happens via Environment.Exit(0) in
        // the tray icon's Exit command.
        appWindow.Closing += (s, e) => e.Cancel = true;

        // Hook WndProc for cross-process PostMessage IPC
        _wndProcDelegate = WndProc;
        SetWindowSubclass(_hwnd, _wndProcDelegate, 0, 0);

        // Deferred init: apply theme, tray icon
        ApplyDeferredInit();
        EnsureStartMenuShortcuts();
        EnsureFlyoutShortcut();
        UpdateShortcutIcons();
        FlyoutWindow.WarmUp(this, SettingsManager.Current.Launchers);
        _ = StartAutoSyncAsync();
        _ = FetchMissingIconsOnStartupAsync();

        // Register for Windows toast notifications
        Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register();
        _ = CheckForUpdateOnStartupAsync();

        // Tell Windows to include --silent when auto-restarting the app
        // (e.g. "Restart apps" after sign-in). Without this, Windows
        // relaunches the exe with no args, which opens the Settings window.
        RegisterApplicationRestart("--silent", 0);

        // Listen for OS theme changes to refresh icons
        _lastDarkTheme = Classes.ThemeManager.IsDarkTheme();
        _uiSettings.ColorValuesChanged += OnSystemThemeChanged;

        if (!IsSilentLaunch())
            SettingsWindow.ShowInstance(this);
    }

    private AppWindow GetAppWindow()
    {
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    /// <summary>
    /// Finish initialization that needs the window to be set up.
    /// </summary>
    private void ApplyDeferredInit()
    {
        // Apply theme
        ThemeManager.ApplySavedTheme(this);

        // ── Tray icons (one per launcher) ──────────────────────────
        SetupTrayIcons();

        var asm = typeof(MainWindow).Assembly.GetName();
        SettingsManager.Current.LastKnownVersion = $"v{asm.Version!.Major}.{asm.Version.Minor}.{asm.Version.Build}";

        // Ensure existing Windows startup registry entry includes --silent
        MigrateStartupRegistryEntry();
    }

    private void SetupTrayIcons()
    {
        _wmTrayCallback = RegisterWindowMessage("LittleLauncher_TrayCallback");
        foreach (var launcher in SettingsManager.Current.Launchers)
            CreateTrayIconForLauncher(launcher);
        SaveSettingsIconToAppData();
    }

    private void CreateTrayIconForLauncher(Launcher launcher)
    {
        // Register per-launcher window message for flyout signaling (from companion exe)
        int wmFlyout = RegisterWindowMessage($"LittleLauncher_ShowFlyout_{launcher.Id}");
        _wmShowFlyoutPerLauncher[launcher.Id] = wmFlyout;

        uint uid = _nextIconId++;
        var icon = ResolveTrayIcon(launcher);
        SaveResolvedIconToAppData(launcher);

        var entry = new TrayIconEntry { Uid = uid, Icon = icon };
        _trayIcons[launcher.Id] = entry;

        if (!launcher.NIconHide)
            AddNativeIcon(entry, launcher.Name);

        // Subscribe to launcher property changes to live-update the icon
        launcher.PropertyChanged += (s, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Launcher.TrayIconMode):
                case nameof(Launcher.CustomTrayIconPath):
                    DispatcherQueue.TryEnqueue(() => UpdateTrayIcon(launcher));
                    break;
                case nameof(Launcher.NIconHide):
                    DispatcherQueue.TryEnqueue(() => UpdateTrayIconVisibility(launcher));
                    break;
                case nameof(Launcher.Name):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateTrayIconTooltip(launcher);
                        EnsureFlyoutStartMenuShortcuts();
                    });
                    break;
            }
        };
    }

    private void AddNativeIcon(TrayIconEntry entry, string? tooltip)
    {
        var tip = (tooltip ?? "");
        if (tip.Length > 127) tip = tip[..127];
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = entry.Uid,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = (uint)_wmTrayCallback,
            hIcon = entry.Icon?.Handle ?? IntPtr.Zero,
            szTip = tip,
        };
        Shell_NotifyIcon(NIM_ADD, ref data);
        entry.IsAdded = true;
    }

    private void RemoveNativeIcon(TrayIconEntry entry)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = entry.Uid,
            szTip = "",
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        entry.IsAdded = false;
    }

    private void CleanupTrayIcons()
    {
        foreach (var entry in _trayIcons.Values)
        {
            if (entry.IsAdded)
                RemoveNativeIcon(entry);
            entry.Icon?.Dispose();
        }
        _trayIcons.Clear();
    }

    private void ShowContextMenu(int x, int y, string? launcherId = null)
    {
        var popup = new H.NotifyIcon.Core.PopupMenu();

        // "Edit Launcher" — drill into this launcher's items page
        if (launcherId != null)
        {
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            if (launcher != null)
            {
                var editItem = new H.NotifyIcon.Core.PopupMenuItem { Text = $"Edit {launcher.Name}" };
                editItem.Click += (s, e) =>
                {
                    SettingsWindow.ShowInstance(this);
                    var sw = SettingsWindow.GetCurrent();
                    sw?.DispatcherQueue.TryEnqueue(() => sw.NavigateToLauncherItems(launcher));
                };
                popup.Items.Add(editItem);
            }
        }

        var settingsItem = new H.NotifyIcon.Core.PopupMenuItem { Text = "Settings" };
        settingsItem.Click += (s, e) => SettingsWindow.ShowInstance(this);
        popup.Items.Add(settingsItem);
        popup.Items.Add(new H.NotifyIcon.Core.PopupMenuSeparator());
        var exitItem = new H.NotifyIcon.Core.PopupMenuItem { Text = "Exit" };
        exitItem.Click += (s, e) =>
        {
            SettingsManager.SaveSettings();
            try { Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Unregister(); } catch { }
            CleanupTrayIcons();
            Environment.Exit(0);
        };
        popup.Items.Add(exitItem);
        popup.Show(_hwnd, x, y);
    }

    /// <summary>
    /// Rebuilds tray icons for all launchers. Removes icons for deleted launchers,
    /// re-creates icons for new launchers, and re-subscribes PropertyChanged handlers
    /// in case launcher objects were replaced (e.g. by SFTP sync).
    /// </summary>
    public void RefreshTrayIcons()
    {
        var currentIds = SettingsManager.Current.Launchers.Select(l => l.Id).ToHashSet();

        // Remove icons for deleted launchers
        foreach (var id in _trayIcons.Keys.ToList())
        {
            if (!currentIds.Contains(id))
            {
                var entry = _trayIcons[id];
                if (entry.IsAdded) RemoveNativeIcon(entry);
                entry.Icon?.Dispose();
                _trayIcons.Remove(id);
                _wmShowFlyoutPerLauncher.Remove(id);
            }
        }

        // Create icons for new launchers
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            if (!_trayIcons.ContainsKey(launcher.Id))
                CreateTrayIconForLauncher(launcher);
        }

        SaveSettingsIconToAppData();
        FlyoutWindow.WarmUp(this, SettingsManager.Current.Launchers);
    }

    /// <summary>
    /// Resolves the tray icon for a specific launcher based on its TrayIconMode setting.
    /// </summary>
    private static System.Drawing.Icon? ResolveTrayIcon(Launcher launcher)
    {
        try
        {
            using var bitmap = ResolveBaseIconBitmap(launcher);
            if (bitmap != null)
                return BitmapToIcon(bitmap);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to resolve tray icon for launcher {Name}, falling back to Blue", launcher.Name);
        }

        // Last-resort fallback: load the embedded exe icon
        string fallbackIco = Path.Combine(AppContext.BaseDirectory, "Resources", "LittleLauncher.ico");
        return File.Exists(fallbackIco) ? new System.Drawing.Icon(fallbackIco, 64, 64) : null;
    }

    /// <summary>
    /// Preset icon PNG filenames (in Resources/AppIcons/), keyed by TrayIconMode string.
    /// </summary>
    private static readonly Dictionary<string, string> PresetIcons = new()
    {
        { TrayIconModes.Blue, "Blue" },
        { TrayIconModes.Green, "Green" },
        { TrayIconModes.Teal, "Teal" },
        { TrayIconModes.Red, "Red" },
        { TrayIconModes.Orange, "Orange" },
        { TrayIconModes.Purple, "Purple" },
    };

    /// <summary>
    /// Glyph preset icon characters (Segoe Fluent Icons), keyed by TrayIconMode string.
    /// </summary>
    private static readonly Dictionary<string, (char Glyph, string Label)> GlyphPresets = new()
    {
        { TrayIconModes.Pin, ('\uE840', "Pin") },
        { TrayIconModes.Star, ('\uE734', "Star") },
        { TrayIconModes.Heart, ('\uEB51', "Heart") },
        { TrayIconModes.Lightning, ('\uE945', "Lightning") },
        { TrayIconModes.Search, ('\uE721', "Search") },
        { TrayIconModes.Globe, ('\uE774', "Globe") },
    };

    /// <summary>
    /// Single source of truth for rendering a launcher's icon as a 256×256 bitmap.
    /// All icon surfaces (tray, shortcuts, settings window) derive from this.
    /// </summary>
    private static System.Drawing.Bitmap? ResolveBaseIconBitmap(Launcher launcher)
    {
        string mode = launcher.TrayIconMode;

        try
        {
            // Composite mode — 2×2 grid of first 4 item icons
            if (mode == TrayIconModes.Composite)
                return RenderCompositeIconBitmap(launcher);

            // Custom user image
            if (mode == TrayIconModes.Custom)
            {
                string path = launcher.CustomTrayIconPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using var ico = new System.Drawing.Icon(path, 256, 256);
                        return new System.Drawing.Bitmap(ico.ToBitmap(), 256, 256);
                    }
                    using var original = new System.Drawing.Bitmap(path);
                    return TrimAndResizeTo256(original);
                }
            }

            // Glyph presets
            if (GlyphPresets.TryGetValue(mode, out var preset))
            {
                bool dark = ThemeManager.IsDarkTheme();
                var fg = dark ? System.Drawing.Color.White : System.Drawing.Color.Black;
                return RenderGlyphBitmap(preset.Glyph, fg);
            }

            // Gallery-chosen glyph/emoji (stored as "Glyph:X" or "Glyph:#RRGGBB:X" in TrayIconMode)
            if (TrayIconModes.IsGlyphMode(mode))
            {
                string? glyphStr = TrayIconModes.GetGlyphCharacter(mode);
                if (!string.IsNullOrEmpty(glyphStr) && glyphStr.Length > 0)
                {
                    bool dark = ThemeManager.IsDarkTheme();
                    string? colorHex = TrayIconModes.GetGlyphColor(mode);
                    var fg = !string.IsNullOrEmpty(colorHex)
                        ? ParseHexColor(colorHex, dark ? System.Drawing.Color.White : System.Drawing.Color.Black)
                        : (dark ? System.Drawing.Color.White : System.Drawing.Color.Black);
                    string fontName = Classes.IconGallery.IsFluentGlyph(glyphStr)
                        ? "Segoe Fluent Icons"
                        : "Segoe UI Emoji";
                    return RenderGlyphBitmap(glyphStr[0], fg, 256, 240f, fontName);
                }
            }

            // Color presets (fallback to Blue if unknown)
            if (!PresetIcons.TryGetValue(mode, out var name))
                name = PresetIcons[TrayIconModes.Blue];
            string pngPath = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", $"{name}.png");
            if (File.Exists(pngPath))
            {
                using var original = new System.Drawing.Bitmap(pngPath);
                return TrimAndResizeTo256(original);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to resolve base icon bitmap for mode {Mode}", mode);
        }
        return null;
    }

    /// <summary>
    /// Renders a Segoe Fluent Icons glyph as a 256×256 bitmap.
    /// </summary>
    private static System.Drawing.Bitmap RenderGlyphBitmap(char glyph, System.Drawing.Color fg)
    {
        return RenderGlyphBitmap(glyph, fg, 256, 240f);
    }

    /// <summary>Parses "#RRGGBB" to a System.Drawing.Color, returning fallback on failure.</summary>
    private static System.Drawing.Color ParseHexColor(string hex, System.Drawing.Color fallback)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                int r = Convert.ToByte(hex[..2], 16);
                int g = Convert.ToByte(hex[2..4], 16);
                int b = Convert.ToByte(hex[4..6], 16);
                return System.Drawing.Color.FromArgb(255, r, g, b);
            }
        }
        catch { /* fall through */ }
        return fallback;
    }

    /// <summary>
    /// Renders a Segoe Fluent Icons glyph at a specified size.
    /// </summary>
    private static System.Drawing.Bitmap RenderGlyphBitmap(char glyph, System.Drawing.Color fg, int size, float fontPx, string fontName = "Segoe Fluent Icons")
    {
        var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var font = new System.Drawing.Font(fontName, fontPx, System.Drawing.GraphicsUnit.Pixel);
            using var brush = new System.Drawing.SolidBrush(fg);
            using var fmt = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic);
            fmt.Alignment = System.Drawing.StringAlignment.Center;
            fmt.LineAlignment = System.Drawing.StringAlignment.Center;
            g.DrawString(glyph.ToString(), font, brush, new System.Drawing.RectangleF(0, 0, size, size), fmt);
        }
        return bitmap;
    }

    /// <summary>
    /// Renders a 2×2 composite icon from the first 4 launchable items, similar to
    /// how Windows 11 Start menu shows folder/group previews.
    /// </summary>
    private static System.Drawing.Bitmap? RenderCompositeIconBitmap(Launcher launcher)
    {
        const int canvasSize = 256;
        const int cellSize = 124;  // each sub-icon cell
        const int gap = 4;         // gap between cells

        // Collect first 4 launchable items (flatten groups)
        var items = new List<LauncherItem>();
        CollectLaunchableItems(launcher.Items, items, 4);
        if (items.Count == 0) return null;

        // 2×2 grid positions, centered on canvas
        int gridWidth = cellSize * 2 + gap;
        int offsetX = (canvasSize - gridWidth) / 2;
        int offsetY = (canvasSize - gridWidth) / 2;
        var positions = new (int X, int Y)[]
        {
            (offsetX, offsetY),
            (offsetX + cellSize + gap, offsetY),
            (offsetX, offsetY + cellSize + gap),
            (offsetX + cellSize + gap, offsetY + cellSize + gap),
        };

        bool dark = ThemeManager.IsDarkTheme();
        var bitmap = new System.Drawing.Bitmap(canvasSize, canvasSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.Clear(System.Drawing.Color.Transparent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        for (int i = 0; i < Math.Min(items.Count, 4); i++)
        {
            var item = items[i];
            var pos = positions[i];

            // Try to load the item's icon
            System.Drawing.Bitmap? subIcon = null;
            try
            {
                if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
                {
                    if (item.IconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using var ico = new System.Drawing.Icon(item.IconPath, 64, 64);
                        subIcon = new System.Drawing.Bitmap(ico.ToBitmap(), cellSize, cellSize);
                    }
                    else
                    {
                        using var orig = new System.Drawing.Bitmap(item.IconPath);
                        subIcon = new System.Drawing.Bitmap(orig, cellSize, cellSize);
                    }
                }
                else if (!string.IsNullOrEmpty(item.IconGlyph) && item.IconGlyph.Length > 0)
                {
                    var fg = !string.IsNullOrEmpty(item.IconColor)
                        ? ParseHexColor(item.IconColor, dark ? System.Drawing.Color.White : System.Drawing.Color.Black)
                        : (dark ? System.Drawing.Color.White : System.Drawing.Color.Black);
                    string fontName = Classes.IconGallery.IsFluentGlyph(item.IconGlyph)
                        ? "Segoe Fluent Icons"
                        : "Segoe UI Emoji";
                    subIcon = RenderGlyphBitmap(item.IconGlyph[0], fg, cellSize, cellSize * 0.8f, fontName);
                }
            }
            catch { /* best-effort */ }

            if (subIcon == null) continue;

            using (subIcon)
            {
                g.DrawImage(subIcon,
                    new System.Drawing.Rectangle(pos.X, pos.Y, cellSize, cellSize),
                    new System.Drawing.Rectangle(0, 0, subIcon.Width, subIcon.Height),
                    System.Drawing.GraphicsUnit.Pixel);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// Collects the first N launchable (non-group, non-heading, non-column-break) items,
    /// flattening groups.
    /// </summary>
    private static void CollectLaunchableItems(IEnumerable<LauncherItem> source, List<LauncherItem> result, int max)
    {
        foreach (var item in source)
        {
            if (result.Count >= max) return;
            if (item.IsColumnBreak) continue;
            if (item.IsGroup)
            {
                CollectLaunchableItems(item.Children, result, max);
                continue;
            }
            // Headings have no Path and aren't launchable
            if (string.IsNullOrEmpty(item.Path)) continue;
            result.Add(item);
        }
    }

    /// <summary>Creates a GraphicsPath for a rounded rectangle.</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(int x, int y, int w, int h, int r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Trims transparent padding and centers the content on a 256×256 canvas,
    /// preserving the original aspect ratio.
    /// </summary>
    private static System.Drawing.Bitmap TrimAndResizeTo256(System.Drawing.Bitmap original)
    {
        var bounds = GetOpaqueContentBounds(original);
        int contentSize = Math.Max(bounds.Width, bounds.Height);
        if (contentSize <= 0) contentSize = original.Width;

        // Fill 100% of the canvas so icons appear full-size in tray/taskbar
        float scale = 256f / contentSize;
        int drawW = (int)(bounds.Width * scale);
        int drawH = (int)(bounds.Height * scale);
        int offsetX = (256 - drawW) / 2;
        int offsetY = (256 - drawH) / 2;

        const int iconSize = 256;
        var resized = new System.Drawing.Bitmap(iconSize, iconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(resized))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.Clear(System.Drawing.Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            // Draw only the trimmed content region, preserving aspect ratio
            var dest = new System.Drawing.Rectangle(offsetX, offsetY, drawW, drawH);
            g.DrawImage(original, dest, bounds, System.Drawing.GraphicsUnit.Pixel);
        }
        return resized;
    }

    /// <summary>
    /// Returns the bounding rectangle of non-transparent pixels in a bitmap.
    /// </summary>
    private static System.Drawing.Rectangle GetOpaqueContentBounds(System.Drawing.Bitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = 0, maxY = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).A > 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < minX) return new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        return new System.Drawing.Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// Converts a Bitmap to an Icon via an in-memory multi-resolution ICO stream.
    /// Produces entries at 16, 24, 32, 48, 64, and 256 pixels so the system tray
    /// can pick the correct size for the current DPI.
    /// </summary>
    private static System.Drawing.Icon BitmapToIcon(System.Drawing.Bitmap bitmap)
    {
        int[] sizes = [16, 24, 32, 48, 64, 256];
        byte[][] pngEntries = new byte[sizes.Length][];

        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            using var resized = new System.Drawing.Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(resized))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(bitmap, 0, 0, s, s);
            }
            using var pngStream = new MemoryStream();
            resized.Save(pngStream, ImageFormat.Png);
            pngEntries[i] = pngStream.ToArray();
        }

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ICO header
            bw.Write((short)0);                     // reserved
            bw.Write((short)1);                     // type: icon
            bw.Write((short)sizes.Length);           // image count

            // Directory entries — offset starts after header + all directory entries
            int dataOffset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));  // width
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));  // height
                bw.Write((byte)0);            // color palette
                bw.Write((byte)0);            // reserved
                bw.Write((short)1);           // color planes
                bw.Write((short)32);          // bits per pixel
                bw.Write(pngEntries[i].Length); // image data size
                bw.Write(dataOffset);         // offset to image data

                dataOffset += pngEntries[i].Length;
            }

            // PNG payloads
            for (int i = 0; i < sizes.Length; i++)
                bw.Write(pngEntries[i]);
        }

        ms.Position = 0;
        return new System.Drawing.Icon(ms);
    }

    /// <summary>
    /// Ensures the per-launcher .ico file exists in AppData. Called before
    /// pinning to the taskbar so the companion exe and Start Menu shortcut
    /// can reference the correct icon.
    /// </summary>
    internal static void EnsureLauncherIconSaved(Launcher launcher) =>
        SaveResolvedIconToAppData(launcher);

    /// <summary>
    /// Persists a launcher's resolved icon as an .ico file in AppData so that
    /// shortcuts (Start Menu, pinned taskbar) can reference it.
    /// Returns the path to the saved .ico, or null on failure.
    /// The first launcher's icon is also saved as the canonical app-icon.ico.
    /// </summary>
    private static string? SaveResolvedIconToAppData(Launcher launcher)
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            Directory.CreateDirectory(appDataDir);

            // Per-launcher icon file
            string launcherIcoPath = Path.Combine(appDataDir, $"app-icon-{launcher.Id}.ico");

            using var bitmap = ResolveBaseIconBitmap(launcher);
            if (bitmap == null) return null;

            using var icon = BitmapToIcon(bitmap);
            using (var stream = new FileStream(launcherIcoPath, FileMode.Create, FileAccess.Write))
                icon.Save(stream);

            // Also update the canonical app-icon.ico if this is the first (or only) launcher
            var firstLauncher = SettingsManager.Current.Launchers.FirstOrDefault();
            if (firstLauncher != null && firstLauncher.Id == launcher.Id)
            {
                string canonicalPath = Path.Combine(appDataDir, "app-icon.ico");
                File.Copy(launcherIcoPath, canonicalPath, overwrite: true);
            }

            return launcherIcoPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to save resolved icon to AppData for launcher {Name}", launcher.Name);
        }
        return null;
    }

    /// <summary>
    /// Generates a settings-specific icon by compositing the app's blue rocket icon
    /// with a small gear overlay in the bottom-right corner.
    /// Saved as settings-icon.ico alongside app-icon.ico.
    /// </summary>
    internal static string? SaveSettingsIconToAppData()
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            Directory.CreateDirectory(appDataDir);
            string icoPath = Path.Combine(appDataDir, "settings-icon.ico");

            // Always use the blue rocket (app identity) as the base icon
            string bluePng = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", "Blue.png");
            if (!File.Exists(bluePng)) return null;
            using var original = new System.Drawing.Bitmap(bluePng);
            using var baseBitmap = TrimAndResizeTo256(original);
            if (baseBitmap == null) return null;

            // Draw gear overlay in the bottom-right corner
            const int overlaySize = 112; // ~44% of 256
            const int padding = 4;
            using (var g = System.Drawing.Graphics.FromImage(baseBitmap))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                // Semi-transparent dark circle background for contrast
                int cx = 256 - overlaySize / 2 - padding;
                int cy = 256 - overlaySize / 2 - padding;
                using var bgBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, 30, 30, 30));
                g.FillEllipse(bgBrush, cx - overlaySize / 2, cy - overlaySize / 2, overlaySize, overlaySize);

                // Gear glyph (\uE713 = Settings in Segoe Fluent Icons)
                using var font = new System.Drawing.Font("Segoe Fluent Icons", overlaySize * 0.7f, System.Drawing.GraphicsUnit.Pixel);
                using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                using var fmt = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic);
                fmt.Alignment = System.Drawing.StringAlignment.Center;
                fmt.LineAlignment = System.Drawing.StringAlignment.Center;
                var rect = new System.Drawing.RectangleF(
                    cx - overlaySize / 2f, cy - overlaySize / 2f,
                    overlaySize, overlaySize);
                g.DrawString("\uE713", font, brush, rect, fmt);
            }

            using var icon = BitmapToIcon(baseBitmap);
            using var stream = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
            icon.Save(stream);
            return icoPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to save settings icon to AppData");
        }
        return null;
    }

    /// <summary>
    /// Updates the tray icon for a specific launcher and refreshes associated shortcuts.
    /// Called from the launcher's PropertyChanged handler.
    /// </summary>
    internal void UpdateTrayIcon(Launcher launcher)
    {
        if (!_trayIcons.TryGetValue(launcher.Id, out var entry)) return;
        entry.Icon?.Dispose();
        entry.Icon = ResolveTrayIcon(launcher);
        if (entry.IsAdded)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = entry.Uid,
                uFlags = NIF_ICON,
                hIcon = entry.Icon?.Handle ?? IntPtr.Zero,
                szTip = "",
            };
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        SaveResolvedIconToAppData(launcher);
        UpdateShortcutIcons();
        SaveSettingsIconToAppData();
        SettingsWindow.GetCurrent()?.RefreshIcon();
    }

    internal void UpdateTrayIconVisibility(Launcher launcher)
    {
        if (!_trayIcons.TryGetValue(launcher.Id, out var entry)) return;
        if (launcher.NIconHide && entry.IsAdded)
            RemoveNativeIcon(entry);
        else if (!launcher.NIconHide && !entry.IsAdded)
            AddNativeIcon(entry, launcher.Name);
    }

    private void UpdateTrayIconTooltip(Launcher launcher)
    {
        if (!_trayIcons.TryGetValue(launcher.Id, out var entry) || !entry.IsAdded) return;
        var tip = (launcher.Name ?? "");
        if (tip.Length > 127) tip = tip[..127];
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = entry.Uid,
            uFlags = NIF_TIP,
            szTip = tip,
        };
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void OnSystemThemeChanged(global::Windows.UI.ViewManagement.UISettings sender, object args)
    {
        bool dark = Classes.ThemeManager.IsDarkTheme();
        if (dark == _lastDarkTheme) return;
        _lastDarkTheme = dark;

        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var launcher in SettingsManager.Current.Launchers)
                UpdateTrayIcon(launcher);
        });
    }

    private async Task StartAutoSyncAsync()
    {
        await AutoSyncService.SyncOnStartupAsync();
        AutoSyncService.Start();
    }

    /// <summary>
    /// Scans launcher items on startup and fetches any missing icons.
    /// Covers settings-import-then-restart and machine-migration scenarios.
    /// </summary>
    private static async Task FetchMissingIconsOnStartupAsync()
    {
        try
        {
            // Check across all launchers' items for missing icons
            bool anyMissing = false;
            foreach (var launcher in SettingsManager.Current.Launchers)
            {
                var allItems = launcher.Items.SelectMany(i => i.IsGroup ? new[] { i }.Concat(i.Children) : [i]);
                if (allItems.Any(i =>
                    !i.IsGroup &&
                    !string.IsNullOrWhiteSpace(i.Path) &&
                    (string.IsNullOrEmpty(i.IconPath) || !File.Exists(i.IconPath))))
                {
                    anyMissing = true;
                    break;
                }
            }

            if (!anyMissing) return;

            foreach (var launcher in SettingsManager.Current.Launchers)
                await FaviconService.FetchMissingItemIconsAsync(launcher.Items);

            SettingsManager.SaveSettings();
            FlyoutWindow.InvalidateAllItems();
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Startup icon fetch failed");
        }
    }

    /// <summary>
    /// Checks for app updates on startup and shows a toast notification
    /// if a newer version is available. The result is cached in
    /// <see cref="UpdateService.LatestResult"/> for the HomePage to consume.
    /// </summary>
    private async Task CheckForUpdateOnStartupAsync()
    {
        try
        {
            var result = await UpdateService.CheckForUpdateAsync();
            if (result is not { UpdateAvailable: true }) return;

            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText("Update Available")
                .AddText($"Little Launcher {result.LatestVersion} is available. You are running {result.CurrentVersion}.");
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Startup update check failed");
        }
    }

    // ── WndProc — handle cross-process PostMessage IPC ────────────

    /// <summary>
    /// Returns true when the app should start silently (tray only, no Settings window).
    /// Silent launch happens on Windows startup and when the companion exe cold-starts the app.
    /// </summary>
    private static bool IsSilentLaunch()
    {
        // Command-line flag used by unpackaged startup (registry Run key)
        // and companion exe cold-start.
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--silent", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // MSIX StartupTask activation. The manifest declares a StartupTask
        // with no way to pass args, so detect it via the AppLifecycle API.
        // In non-MSIX builds, GetActivatedEventArgs() throws — the catch
        // handles that gracefully.
        try
        {
            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask)
                return true;
        }
        catch { /* Not available or not packaged — treat as normal launch */ }

        return false;
    }

    /// <summary>
    /// Ensures the Windows startup registry entry includes --silent so the app
    /// starts silently on login. Migrates existing entries that lack the flag.
    /// Runs unconditionally so entries created by old versions (before --silent
    /// was added) are fixed regardless of the in-app Startup setting.
    /// </summary>
    private static void MigrateStartupRegistryEntry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            const string appName = "Little Launcher";
            var currentValue = key.GetValue(appName) as string;
            if (currentValue != null && !currentValue.Contains("--silent", StringComparison.OrdinalIgnoreCase))
            {
                var executablePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
                    key.SetValue(appName, $"\"{executablePath}\" --silent");
            }
        }
        catch { /* best-effort migration */ }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        // Handle tray icon left/right click callbacks from Shell_NotifyIcon
        if ((int)msg == _wmTrayCallback && _wmTrayCallback != 0)
        {
            uint uid = (uint)(wParam.ToInt64() & 0xFFFFFFFF);
            int notification = (int)(lParam.ToInt64() & 0xFFFF);

            string? targetLauncherId = null;
            foreach (var (lid, entry) in _trayIcons)
            {
                if (entry.Uid == uid) { targetLauncherId = lid; break; }
            }

            if (targetLauncherId != null)
            {
                if (notification == WM_LBUTTONUP)
                {
                    GetCursorPos(out var pt);
                    string lid = targetLauncherId;
                    DispatcherQueue.TryEnqueue(() => FlyoutWindow.Toggle(this, pt.X, pt.Y, lid));
                }
                else if (notification == WM_RBUTTONUP || notification == WM_CONTEXTMENU)
                {
                    GetCursorPos(out var pt);
                    int cx = pt.X, cy = pt.Y;
                    string lid = targetLauncherId;
                    DispatcherQueue.TryEnqueue(() => ShowContextMenu(cx, cy, lid));
                }
            }
            return IntPtr.Zero;
        }

        // Iterate per-launcher window messages (from companion exe / LauncherShortcut)
        foreach (var (launcherId, wmShowFlyout) in _wmShowFlyoutPerLauncher)
        {
            if ((int)msg == wmShowFlyout && wmShowFlyout != 0)
            {
                // Resolve which launcher to show: use named launcherId, fall back to first launcher
                var targetId = launcherId;
                if (string.IsNullOrEmpty(targetId))
                    targetId = SettingsManager.Current.Launchers.FirstOrDefault()?.Id ?? string.Empty;
                DispatcherQueue.TryEnqueue(() => FlyoutWindow.Toggle(this, (int)wParam, (int)lParam, targetId));
                return IntPtr.Zero;
            }
        }

        if ((int)msg == _wmShowSettings && _wmShowSettings != 0)
        {
            DispatcherQueue.TryEnqueue(() => SettingsWindow.ShowInstance(this));
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    // ── Shortcut management ──────────────────────────────────────────

    /// <summary>
    /// Returns the icon location string for shortcuts.
    /// Uses the saved AppData icon if custom, otherwise the app exe icon.
    /// </summary>
    private static string GetShortcutIconLocation(string fallback)
    {
        string appDataIcon = Path.Combine(
            GetPhysicalAppDataDir(), "app-icon.ico");
        return File.Exists(appDataIcon) ? $"{appDataIcon},0" : fallback;
    }

    private static void EnsureStartMenuShortcuts()
    {
        // MSIX packages register their own Start Menu entry via the manifest.
        // Creating a .lnk would duplicate it.
        if (IsPackaged) return;

        try
        {
            string startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");

            string exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return;

            Directory.CreateDirectory(startMenuDir);

            // Remove old-named shortcuts (renamed to "Little Launcher")
            foreach (var old in new[] { "LittleLauncher Settings.lnk", "LittleLauncher.lnk" })
            {
                string oldLnk = Path.Combine(startMenuDir, old);
                if (File.Exists(oldLnk))
                    File.Delete(oldLnk);
            }

            // Remove MSI-era shortcut that lived in a "Little Launcher" subfolder
            string msiSubfolder = Path.Combine(startMenuDir, "Little Launcher");
            if (Directory.Exists(msiSubfolder))
            {
                try { Directory.Delete(msiSubfolder, true); }
                catch { /* best-effort */ }
            }

            // Single Start Menu shortcut — always opens settings when clicked.
            // Always use the exe's embedded icon (blue rocket) for the app identity.
            CreateOrUpdateShortcut(
                Path.Combine(startMenuDir, "Little Launcher.lnk"),
                exePath,
                "--settings",
                "Little Launcher",
                $"{exePath},0");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create Start Menu shortcuts");
        }

        EnsureFlyoutStartMenuShortcuts();
    }

    /// <summary>
    /// Creates (or updates) a Start Menu shortcut per launcher, targeting the
    /// companion flyout exe with --launcher {guid}.  Each shortcut carries a
    /// PKEY_AppUserModel_ID that matches the AUMID the companion exe sets on
    /// itself via SetCurrentProcessExplicitAppUserModelID.  When the user pins
    /// the companion's taskbar button, Windows finds this matching shortcut and
    /// uses its target/args/icon for the persistent pin.
    /// </summary>
    internal static void EnsureFlyoutStartMenuShortcuts()
    {
        if (IsPackaged) return;

        try
        {
            string startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");
            Directory.CreateDirectory(startMenuDir);

            string flyoutExe = Path.Combine(GetPhysicalAppDataDir(), "LittleLauncherFlyout.exe");
            if (!File.Exists(flyoutExe)) return;

            // Build a set of expected shortcut filenames so we can detect
            // renamed launchers and remove their old shortcuts.
            var expectedNames = new HashSet<string>(
                SettingsManager.Current.Launchers.Select(l =>
                    $"Little Launcher Flyout - {SanitizeForFileName(l.Name)}.lnk"),
                StringComparer.OrdinalIgnoreCase);

            var currentIds = new HashSet<string>(
                SettingsManager.Current.Launchers.Select(l => l.Id),
                StringComparer.OrdinalIgnoreCase);

            // Create/update one shortcut per launcher FIRST, so the new shortcut
            // (with the same AUMID) exists before we delete the old one.
            // This prevents taskbar pins from being dropped during renames.
            foreach (var launcher in SettingsManager.Current.Launchers)
            {
                string iconPath = Path.Combine(GetPhysicalAppDataDir(), $"app-icon-{launcher.Id}.ico");
                if (!File.Exists(iconPath))
                    iconPath = Path.Combine(GetPhysicalAppDataDir(), "app-icon.ico");

                // Sanitize launcher name for filesystem use.
                string safeName = SanitizeForFileName(launcher.Name);
                string lnkPath = Path.Combine(startMenuDir, $"Little Launcher Flyout - {safeName}.lnk");

                CreateOrUpdateShortcut(
                    lnkPath,
                    flyoutExe,
                    $"--launcher {launcher.Id}",
                    $"Open {launcher.Name} flyout",
                    File.Exists(iconPath) ? $"{iconPath},0" : $"{flyoutExe},0");

                // Stamp the AUMID so the shell matches this shortcut to the
                // running companion exe when the user pins it.
                SetShortcutAppUserModelId(lnkPath, $"LittleLauncher.Flyout.{launcher.Id}");

                // Notify the shell about this specific shortcut so it re-reads
                // the AUMID property before the companion exe is launched.
                NotifyShellItemUpdated(lnkPath);
            }

            // Now remove stale flyout shortcuts: deleted launchers OR renamed ones.
            foreach (var lnk in Directory.GetFiles(startMenuDir, "Little Launcher Flyout *.lnk"))
            {
                if (IsFlyoutShortcut(lnk))
                {
                    string fileName = Path.GetFileName(lnk);
                    bool isExpected = expectedNames.Contains(fileName);
                    bool hasValidId = currentIds.Any(id => ShortcutContainsArg(lnk, id));
                    // Delete if the launcher was removed OR the shortcut name is stale (renamed)
                    if (!hasValidId || !isExpected)
                    {
                        try { File.Delete(lnk); } catch { }
                    }
                }
            }

            // Broadcast a global association change so the shell re-indexes
            // all shortcut AUMIDs.  Uses SHCNF_FLUSH for synchronous processing
            // — the call blocks until the shell has handled the notification,
            // ensuring the companion exe's AUMID will match correctly.
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create flyout Start Menu shortcuts");
        }
    }

    private static bool ShortcutContainsArg(string lnkPath, string arg)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return false;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return false;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            try
            {
                string args = shortcut.Arguments ?? "";
                return args.Contains(arg, StringComparison.OrdinalIgnoreCase);
            }
            finally { Marshal.ReleaseComObject(shortcut); }
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    private static string SanitizeForFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "Untitled" : result;
    }

    /// <summary>
    /// Copies the companion flyout exe to %AppData%\LittleLauncher\ and creates
    /// a .lnk shortcut in Start Menu\Programs pointing to the AppData copy.
    ///
    /// This gives the companion exe a consistent, non-packaged location so that
    /// pinned taskbar icons always come from the .lnk's IconLocation (app-icon.ico)
    /// and can be updated at runtime. Works identically for WiX, MSIX, and
    /// unpackaged builds.
    /// </summary>
    private static void EnsureFlyoutShortcut()
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            Directory.CreateDirectory(appDataDir);

            // Source: the companion exe next to the main exe
            string sourceExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LittleLauncherFlyout.exe");
            if (!File.Exists(sourceExe))
                return;

            // Destination: %AppData%\LittleLauncher\LittleLauncherFlyout.exe
            string destExe = Path.Combine(appDataDir, "LittleLauncherFlyout.exe");

            // Always overwrite so the companion exe stays current after updates
            File.Copy(sourceExe, destExe, overwrite: true);

            // In debug (framework-dependent) builds, the companion exe also
            // needs its .dll, .deps.json, and .runtimeconfig.json to run.
            // Release builds are Native AOT single-file and don't need these.
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var ext in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                string src = Path.Combine(baseDir, "LittleLauncherFlyout" + ext);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(appDataDir, "LittleLauncherFlyout" + ext), overwrite: true);
            }

            // Write a breadcrumb so the companion exe can launch the main app
            // if it isn't running (FindWindow returns null).
            string mainExePath = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(mainExePath))
                File.WriteAllText(Path.Combine(appDataDir, "main-exe-path.txt"), mainExePath);

            // Remove the old Start Menu shortcut (no longer created — the
            // pin-to-taskbar button in Settings handles pinning directly).
            string oldLnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "Little Launcher Flyout.lnk");
            if (File.Exists(oldLnk))
                File.Delete(oldLnk);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create flyout shortcut");
        }
    }

    /// <summary>
    /// Updates the icon on all known LittleLauncher shortcuts:
    /// Start Menu main shortcut, per-launcher Start Menu flyout shortcuts,
    /// and any pinned taskbar shortcuts.
    /// Uses uniquely-versioned icon filenames (app-icon-{id}-v{tick}.ico) so
    /// the shell's icon cache — which is keyed by (path, index) — always sees
    /// a path it has never cached, guaranteeing a fresh read from disk.
    /// </summary>
    internal void UpdateShortcutIcons()
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            long tick = Environment.TickCount64;

            // Build per-launcher versioned icon paths for cache-busting.
            var launcherVersionedIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var launcher in SettingsManager.Current.Launchers)
            {
                string perLauncherIco = Path.Combine(appDataDir, $"app-icon-{launcher.Id}.ico");
                if (!File.Exists(perLauncherIco)) continue;

                string versionedPath = Path.Combine(appDataDir, $"app-icon-{launcher.Id}-v{tick}.ico");
                File.Copy(perLauncherIco, versionedPath, overwrite: true);
                launcherVersionedIcons[launcher.Id] = versionedPath;
            }

            // Main shortcut uses the first launcher's icon.
            var firstLauncher = SettingsManager.Current.Launchers.FirstOrDefault();
            string mainIconLocation = firstLauncher != null &&
                launcherVersionedIcons.TryGetValue(firstLauncher.Id, out var firstIconPath)
                ? $"{firstIconPath},0"
                : $"{Environment.ProcessPath},0";

            string startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");

            // Update per-launcher Start Menu flyout shortcuts
            // (The main app shortcut always uses the embedded exe icon, so skip it.)
            foreach (string lnk in Directory.GetFiles(startMenuDir, "Little Launcher Flyout *.lnk"))
            {
                string? launcherId = GetFlyoutShortcutLauncherId(lnk);
                if (launcherId == null) continue;
                string iconLocation = launcherVersionedIcons.TryGetValue(launcherId, out var iconPath)
                    ? $"{iconPath},0" : mainIconLocation;
                UpdateShortcutIconLocation(lnk, iconLocation);
                NotifyShellItemUpdated(lnk);
            }

            // Pinned taskbar shortcuts — update ones that target the flyout companion exe
            string taskbarPinDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch",
                "User Pinned", "TaskBar");
            if (Directory.Exists(taskbarPinDir))
            {
                foreach (string lnk in Directory.GetFiles(taskbarPinDir, "*.lnk"))
                {
                    string? launcherId = GetFlyoutShortcutLauncherId(lnk);
                    if (launcherId == null) continue;
                    string iconLocation = launcherVersionedIcons.TryGetValue(launcherId, out var iconPath)
                        ? $"{iconPath},0" : mainIconLocation;
                    UpdateShortcutIconLocation(lnk, iconLocation);
                    NotifyShellItemUpdated(lnk);
                }
            }

            // Clean up old versioned icon files (keep only current tick)
            try
            {
                string tickSuffix = $"-v{tick}.ico";
                foreach (string f in Directory.GetFiles(appDataDir, "app-icon-*-v*.ico"))
                {
                    if (!f.EndsWith(tickSuffix, StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(f); } catch { }
                }
                // Legacy single-icon versioned files
                foreach (string f in Directory.GetFiles(appDataDir, "app-icon-v*.ico"))
                {
                    try { File.Delete(f); } catch { }
                }
                string altPath = Path.Combine(appDataDir, "app-icon-alt.ico");
                if (File.Exists(altPath))
                    try { File.Delete(altPath); } catch { }
            }
            catch { /* best-effort cleanup */ }

            // Broadcast a global association change so that Windows 11's taskbar
            // re-reads pinned shortcut icons. Because we use uniquely-versioned
            // icon file paths, the IconCache.db won't have a stale entry for the
            // new path — the shell must read the fresh icon from disk.
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to update shortcut icons");
        }
    }

    /// <summary>
    /// Sends a targeted SHCNE_UPDATEITEM notification for a specific file path so the
    /// shell invalidates its cached icon for that file. Uses synchronous flush to
    /// ensure the shell processes the notification before we return.
    /// </summary>
    private static void NotifyShellItemUpdated(string path)
    {
        IntPtr pathPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, pathPtr, IntPtr.Zero); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pathPtr); }
    }

    /// <summary>
    /// Returns the default icon location for the flyout companion exe.
    /// </summary>
    private static string GetFlyoutExeIconFallback()
    {
        string flyoutExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LittleLauncherFlyout.exe");
        return File.Exists(flyoutExe) ? $"{flyoutExe},0" : $"{Environment.ProcessPath},0";
    }

    /// <summary>
    /// Checks if a .lnk shortcut targets LittleLauncherFlyout.exe (the companion flyout exe).
    /// </summary>
    private static bool IsFlyoutShortcut(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return false;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return false;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            try
            {
                string target = shortcut.TargetPath ?? "";
                string fileName = Path.GetFileName(target);
                return fileName.Equals("LittleLauncherFlyout.exe", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Checks if a shortcut targets LittleLauncherFlyout.exe and returns the launcher ID
    /// from its <c>--launcher {guid}</c> argument. Returns null if not a flyout shortcut.
    /// </summary>
    private static string? GetFlyoutShortcutLauncherId(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return null;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            try
            {
                string target = shortcut.TargetPath ?? "";
                string fileName = Path.GetFileName(target);
                if (!fileName.Equals("LittleLauncherFlyout.exe", StringComparison.OrdinalIgnoreCase))
                    return null;

                string args = shortcut.Arguments ?? "";
                const string prefix = "--launcher ";
                int idx = args.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                string rest = args[(idx + prefix.Length)..].Trim();
                int spaceIdx = rest.IndexOf(' ');
                return spaceIdx >= 0 ? rest[..spaceIdx] : rest;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    private static void UpdateShortcutIconLocation(string lnkPath, string iconLocation)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            try
            {
                shortcut.IconLocation = iconLocation;
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }

        // Touch the file's last-write time so the shell sees it as modified
        // and re-reads the icon instead of using its cache.
        try { File.SetLastWriteTimeUtc(lnkPath, DateTime.UtcNow); }
        catch { /* best-effort */ }
    }

    private static void CreateOrUpdateShortcut(
        string shortcutPath, string exePath, string? arguments,
        string description, string iconLocation)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return;

        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = exePath;
                if (arguments != null)
                    shortcut.Arguments = arguments;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = description;
                shortcut.IconLocation = iconLocation;
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}