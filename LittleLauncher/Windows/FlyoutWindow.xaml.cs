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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

public partial class FlyoutWindow : Window
{
    private const int ColumnWidth = 175;
    private const int DefaultIconColumnWidth = 260;
    private const int IconCellWidth = 80;
    private const int IconCellHeight = 84;
    private const int IconSize = 32;
    private const int IconColumnChromeWidth = DefaultIconColumnWidth - (IconCellWidth * Launcher.DefaultIconModeIconsPerRow);
    private const int DefaultSmallIconColumnWidth = 136;
    private const int SmallIconCellWidth = 40;
    private const int SmallIconCellHeight = 40;
    private const int SmallIconSize = 20;
    private const int SmallIconColumnChromeWidth = DefaultSmallIconColumnWidth - (SmallIconCellWidth * Launcher.DefaultIconModeIconsPerRow);
    private const int IconGroupHeaderHeight = 30;
    private const int SmallIconGroupSpacing = 6;
    private const double DefaultMinFlyoutHeight = 80;
    private const double SmallIconMinFlyoutHeight = 52;
    private const int ResizeGripWidth = 4;
    private const double SlideDistanceDip = 36;
    private const uint ShowAnimationDurationMs = 200;
    private const uint HideAnimationDurationMs = 160;

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly object BoundsFileLock = new();
    private static readonly ConcurrentDictionary<string, WindowBounds> CachedBounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-launcher flyout window instances (key = Launcher.Id).</summary>
    private static readonly Dictionary<string, FlyoutWindow> _instances = new();

    private DateTime _lastDismissed = DateTime.MinValue;
    private bool _toolWindowStyleApplied;
    private int _lastItemsHash;
    private MainWindow? _owner;
    private LauncherItem? _dragItem;
    private ObservableCollection<LauncherItem>? _dragSourceCollection;
    private ListViewItem? _lastIndicatorContainer;
    private ListView? _lastIndicatorListView;
    private Brush? _lastIndicatorContainerBorderBrush;
    private Thickness _lastIndicatorContainerBorderThickness;
    private Thickness _lastIndicatorContainerPadding;
    private Thickness _lastIndicatorContainerMargin;
    private Brush? _lastIndicatorListBorderBrush;
    private Thickness _lastIndicatorListBorderThickness;
    private ResizeGrip _leftResizeGrip = null!;
    private ResizeGrip _rightResizeGrip = null!;
    private IntPtr _hwnd;
    private SUBCLASSPROC? _wndProcDelegate;
    private bool _isShowing;
    private bool _isHiding;
    private int _animationVersion;
    private bool _isResizingIconWidth;
    private bool _resizeFromLeft;
    private bool _resizeChangedSetting;
    private int _resizeStartCursorX;
    private int _resizeStartIconsPerRow;
    private List<ObservableCollection<LauncherItem>> _columnLists = [];
    private readonly HashSet<ListView> _loadedIconChildLists = [];
    private readonly HashSet<LauncherItem> _syntheticGroups = [];
    private readonly Launcher _launcher;  // The launcher this window displays
    private FlyoutEntranceEdge _lastEntranceEdge = FlyoutEntranceEdge.Bottom;

    private readonly record struct FlyoutPlacement(
        int Left,
        int Top,
        int StartTop,
        int Width,
        int Height,
        FlyoutEntranceEdge Edge);

    private enum FlyoutEntranceEdge
    {
        Top,
        Bottom,
    }

    private static bool AreAnimationsEnabled => SettingsManager.Current.FlyoutAnimationsEnabled;
    private int CurrentViewMode => LauncherViewModes.Normalize(_launcher.ViewMode);
    private bool IsListMode => CurrentViewMode == LauncherViewModes.List;
    private bool IsSmallIconMode => CurrentViewMode == LauncherViewModes.SmallIcon;
    private bool IsIconMode => LauncherViewModes.IsIconMode(CurrentViewMode);

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

        InitializeResizeGrips();
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
        if (_isShowing || _isResizingIconWidth) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            HideFlyout();
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
                if (!instance._isHiding)
                    instance.HideFlyout();
                return;
            }
        }

        if (!instance._isHiding && (DateTime.UtcNow - instance._lastDismissed).TotalMilliseconds < 300)
            return;

        instance._owner = owner;
        instance.RebuildItemsIfNeeded();

        // Calculate DPI-aware dimensions
        double dpiScale = GetDpiForWindow(instance._hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        int flyoutWidthPx = (int)Math.Ceiling(instance.GetFlyoutWidth() * dpiScale);
        int flyoutHeightPx = (int)Math.Ceiling(instance.MeasureContentHeight() * dpiScale);

        instance._isShowing = true;
        var appWindow = instance.GetAppWindow();
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(flyoutWidthPx, flyoutHeightPx));
        if (!instance._toolWindowStyleApplied)
        {
            int exStyle = GetWindowLong(instance._hwnd, GWL_EXSTYLE);
            SetWindowLong(instance._hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            instance._toolWindowStyleApplied = true;
        }

        var placement = instance.CalculatePlacement(screenX, screenY);
        instance._lastEntranceEdge = placement.Edge;
        if (AreAnimationsEnabled)
            instance.ShowAnimated(placement);
        else
            instance.ShowWithoutAnimation(placement);
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
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        if ((GetWindowLong(_hwnd, GWL_STYLE) & WS_VISIBLE) == 0)
            return;

        _lastDismissed = DateTime.UtcNow;
        _animationVersion++;

        if (!AreAnimationsEnabled)
        {
            _isShowing = false;
            _isHiding = false;
            ShowWindow(_hwnd, SW_HIDE);
            return;
        }

        _isHiding = true;
        _isShowing = false;

        GetWindowRect(_hwnd, out var rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        int exitOffset = GetSlideDistancePx();
        int endTop = _lastEntranceEdge == FlyoutEntranceEdge.Top ? rect.Top - exitOffset : rect.Top + exitOffset;
        int animationVersion = ++_animationVersion;

        AnimateWindowPosition(animationVersion, rect.Left, rect.Top, endTop, width, height, HideAnimationDurationMs, hideAtEnd: true);
    }

    private void ShowWithoutAnimation(FlyoutPlacement placement)
    {
        _animationVersion++;
        _isHiding = false;
        _isShowing = true;

        SetWindowPos(_hwnd, 0, placement.Left, placement.Top, placement.Width, placement.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);

        _isShowing = false;
    }

    private void ShowAnimated(FlyoutPlacement placement)
    {
        _isHiding = false;
        _isShowing = true;

        int startTop = placement.StartTop;
        if ((GetWindowLong(_hwnd, GWL_STYLE) & WS_VISIBLE) != 0 && GetWindowRect(_hwnd, out var rect))
            startTop = rect.Top;

        int animationVersion = ++_animationVersion;

        SetWindowPos(_hwnd, 0, placement.Left, startTop, placement.Width, placement.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);

        if (startTop == placement.Top)
        {
            _isShowing = false;
            return;
        }

        AnimateWindowPosition(animationVersion, placement.Left, startTop, placement.Top,
            placement.Width, placement.Height, ShowAnimationDurationMs, hideAtEnd: false);
    }

    private void AnimateWindowPosition(int animationVersion, int left, int startTop, int endTop, int width, int height, uint durationMs, bool hideAtEnd)
    {
        if (startTop == endTop)
        {
            CompleteWindowAnimation(animationVersion, left, endTop, width, height, hideAtEnd);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (animationVersion != _animationVersion || _hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            {
                CompositionTarget.Rendering -= handler;
                return;
            }

            double progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            double eased = hideAtEnd ? EaseInCubic(progress) : EaseOutCubic(progress);
            int currentTop = (int)Math.Round(Lerp(startTop, endTop, eased));

            SetWindowPos(_hwnd, 0, left, currentTop, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

            if (progress >= 1)
            {
                CompositionTarget.Rendering -= handler;
                CompleteWindowAnimation(animationVersion, left, endTop, width, height, hideAtEnd);
            }
        };

        CompositionTarget.Rendering += handler;
    }

    private void CompleteWindowAnimation(int animationVersion, int left, int top, int width, int height, bool hideAtEnd)
    {
        if (animationVersion != _animationVersion || _hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        SetWindowPos(_hwnd, 0, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

        if (hideAtEnd)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _isHiding = false;
        }
        else
        {
            _isShowing = false;
        }
    }

    private int GetSlideDistancePx()
    {
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        return Math.Max(18, (int)Math.Round(SlideDistanceDip * scale));
    }

    private static double Lerp(int start, int end, double progress)
    {
        return start + ((end - start) * progress);
    }

    private static double EaseOutCubic(double progress)
    {
        double inverse = 1 - progress;
        return 1 - (inverse * inverse * inverse);
    }

    private static double EaseInCubic(double progress)
    {
        return progress * progress * progress;
    }

    private AppWindow GetAppWindow()
    {
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    /// <summary>
    /// Sets AUMID and relaunch properties (icon, command, display name) on the
    /// flyout HWND. Called every time the flyout is shown so the taskbar picks up
    /// icon or name changes without requiring unpin+repin.
    /// </summary>
    // ── Content ─────────────────────────────────────────────────────

    private static int ComputeItemsHash(Launcher launcher)
    {
        var items = launcher.Items;
        if (items == null || items.Count == 0) return 0;
        var hash = new HashCode();
        hash.Add(LauncherViewModes.Normalize(launcher.ViewMode));
        hash.Add(Launcher.ClampIconModeIconsPerRow(launcher.IconModeIconsPerRow));
        hash.Add(launcher.ShowTitle);
        hash.Add(launcher.Name);
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
        hash.Add(item.IconColor);
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
        }

        return columns;
    }

    private void WrapUngroupedItemsIntoSyntheticGroups()
    {
        for (int columnIndex = 0; columnIndex < _columnLists.Count; columnIndex++)
        {
            var column = _columnLists[columnIndex];
            var newColumn = new ObservableCollection<LauncherItem>();
            LauncherItem? currentSynthetic = null;

            foreach (var item in column)
            {
                if (item.IsGroup)
                {
                    currentSynthetic = null;
                    newColumn.Add(item);
                    continue;
                }

                if (currentSynthetic == null)
                {
                    currentSynthetic = LauncherItem.CreateGroup(string.Empty);
                    currentSynthetic.IsExpanded = true;
                    _syntheticGroups.Add(currentSynthetic);
                    newColumn.Add(currentSynthetic);
                }

                currentSynthetic.Children.Add(item);
            }

            _columnLists[columnIndex] = newColumn;
        }
    }

    private void SyncColumnsToFlatList()
    {
        _launcher.Items.Clear();

        for (int columnIndex = 0; columnIndex < _columnLists.Count; columnIndex++)
        {
            if (columnIndex > 0)
                _launcher.Items.Add(LauncherItem.CreateColumnBreak());

            foreach (var item in _columnLists[columnIndex])
            {
                if (_syntheticGroups.Contains(item))
                {
                    foreach (var child in item.Children)
                        _launcher.Items.Add(child);
                }
                else
                {
                    _launcher.Items.Add(item);
                }
            }
        }
    }

    private ListView CreateColumnListView(int columnIndex)
    {
        bool isIconMode = IsIconMode;
        string templateSelectorKey = isIconMode
            ? (IsSmallIconMode ? "SmallIconItemTemplateSelector" : "IconItemTemplateSelector")
            : "ListItemTemplateSelector";

        var lv = new ListView
        {
            Width = isIconMode ? GetIconColumnWidth(_columnLists[columnIndex]) : ColumnWidth,
            Padding = isIconMode ? new Thickness(0) : new Thickness(8, 6, 8, 6),
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.None,
            IsTabStop = false,
            CanDragItems = true,
            AllowDrop = true,
            Tag = columnIndex,
            TabNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Once,
            ItemTemplateSelector = (DataTemplateSelector)RootGrid.Resources[templateSelectorKey],
            ItemContainerTransitions = new TransitionCollection(),
            Transitions = new TransitionCollection(),
        };

        if (isIconMode)
            lv.ItemContainerStyle = (Style)RootGrid.Resources["GroupItemContainerStyle"];
        else
            lv.ItemContainerStyleSelector = (StyleSelector)RootGrid.Resources["ItemContainerStyleSelector"];

        ScrollViewer.SetVerticalScrollBarVisibility(lv, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
        lv.Loaded += ColumnListView_Loaded;
        lv.ItemClick += ItemsListControl_ItemClick;
        lv.RightTapped += ItemsListControl_RightTapped;
        lv.DragItemsStarting += ColumnListView_DragItemsStarting;
        lv.DragOver += ColumnListView_DragOver;
        lv.DragLeave += ColumnListView_DragLeave;
        lv.Drop += ColumnListView_Drop;
        lv.DragItemsCompleted += ColumnListView_DragItemsCompleted;
        return lv;
    }

    private static void DisableListViewTransitions(ListView listView)
    {
        listView.ItemContainerTransitions = new TransitionCollection();
        listView.Transitions = new TransitionCollection();

        if (listView.ItemsPanelRoot is Panel panel)
            panel.ChildrenTransitions = new TransitionCollection();
    }

    private void ColumnListView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
            DisableListViewTransitions(listView);
    }

    private void InitializeResizeGrips()
    {
        _leftResizeGrip = CreateResizeGrip(HorizontalAlignment.Left);
        _rightResizeGrip = CreateResizeGrip(HorizontalAlignment.Right);
        RootGrid.Children.Add(_leftResizeGrip);
        RootGrid.Children.Add(_rightResizeGrip);
    }

    private ResizeGrip CreateResizeGrip(HorizontalAlignment alignment)
    {
        var grip = new ResizeGrip
        {
            Width = 10,
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };

        grip.SetValue(Canvas.ZIndexProperty, 10);
        grip.PointerPressed += ResizeGrip_PointerPressed;
        grip.PointerMoved += ResizeGrip_PointerMoved;
        grip.PointerReleased += ResizeGrip_PointerReleased;
        grip.PointerCaptureLost += ResizeGrip_PointerCaptureLost;
        return grip;
    }

    // ── Icon mode rendering ─────────────────────────────────────────

    private int GetIconModeIconsPerRow() => Launcher.ClampIconModeIconsPerRow(_launcher.IconModeIconsPerRow);

    private int GetActiveIconCellWidth() => IsSmallIconMode ? SmallIconCellWidth : IconCellWidth;

    private int GetActiveIconCellHeight() => IsSmallIconMode ? SmallIconCellHeight : IconCellHeight;

    private int GetActiveIconSize() => IsSmallIconMode ? SmallIconSize : IconSize;

    private int GetActiveIconColumnChromeWidth() => IsSmallIconMode ? SmallIconColumnChromeWidth : IconColumnChromeWidth;

    private int GetActiveGroupHeaderHeight() => IsSmallIconMode ? SmallIconGroupSpacing : IconGroupHeaderHeight;

    private double GetMinimumFlyoutHeight() => IsSmallIconMode ? SmallIconMinFlyoutHeight : DefaultMinFlyoutHeight;

    private int GetIconColumnWidth() => GetActiveIconColumnChromeWidth() + (GetIconModeIconsPerRow() * GetActiveIconCellWidth());

    private int GetIconColumnWidth(ObservableCollection<LauncherItem> items)
    {
        int maxIconsPerRow = GetIconModeIconsPerRow();
        int cellWidth = GetActiveIconCellWidth();
        int widestRow = 0;
        int currentRow = 0;

        foreach (var item in items)
        {
            if (item.IsGroup)
            {
                widestRow = Math.Max(widestRow, currentRow);
                currentRow = 0;

                if (item.Children.Count > 0)
                    widestRow = Math.Max(widestRow, Math.Min(maxIconsPerRow, item.Children.Count));

                continue;
            }

            currentRow++;
            if (currentRow >= maxIconsPerRow)
            {
                widestRow = Math.Max(widestRow, maxIconsPerRow);
                currentRow = 0;
            }
        }

        widestRow = Math.Max(widestRow, currentRow);
        widestRow = Math.Max(1, widestRow);
        return GetActiveIconColumnChromeWidth() + (widestRow * cellWidth);
    }

    private int GetFlyoutWidth()
    {
        if (IsListMode)
            return ColumnWidth * Math.Max(1, ColumnsPanel.Children.Count);

        int totalWidth = 0;
        foreach (var column in _columnLists)
            totalWidth += GetIconColumnWidth(column);

        return totalWidth;
    }

    private int GetIconResizeStepWidth()
    {
        int columnCount = Math.Max(1, _columnLists.Count);
        int cellWidth = GetActiveIconCellWidth();
        return Math.Max(cellWidth, columnCount * cellWidth);
    }

    private void UpdateResizeGripVisibility()
    {
        var visibility = IsIconMode ? Visibility.Visible : Visibility.Collapsed;
        _leftResizeGrip.Visibility = visibility;
        _rightResizeGrip.Visibility = visibility;
    }

    private ScrollViewer CreateIconModeColumn(ObservableCollection<LauncherItem> items)
    {
        int iconsPerRow = GetIconModeIconsPerRow();
        var column = new StackPanel { Width = GetIconColumnWidth(items), Padding = new Thickness(8, 6, 8, 6) };
        var currentRow = new StackPanel { Orientation = Orientation.Horizontal };
        int itemsInRow = 0;

        void FlushRow()
        {
            if (currentRow.Children.Count > 0)
            {
                column.Children.Add(currentRow);
                currentRow = new StackPanel { Orientation = Orientation.Horizontal };
                itemsInRow = 0;
            }
        }

        void AddTile(LauncherItem child)
        {
            currentRow.Children.Add(CreateIconTile(child));
            itemsInRow++;
            if (itemsInRow >= iconsPerRow)
                FlushRow();
        }

        foreach (var item in items)
        {
            if (item.IsGroup)
            {
                FlushRow();
                column.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.7,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 8, 0, 4),
                });
            }
            else
            {
                AddTile(item);
            }
        }

        if (currentRow.Children.Count > 0)
            column.Children.Add(currentRow);

        return new ScrollViewer
        {
            Content = column,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private Button CreateIconTile(LauncherItem item)
    {
        UIElement iconElement;
        int iconSize = GetActiveIconSize();
        if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
        {
            var bmp = new BitmapImage { DecodePixelWidth = iconSize + 4 };
            bmp.UriSource = new Uri(item.IconPath, UriKind.Absolute);
            iconElement = new Image
            {
                Source = bmp,
                Width = iconSize,
                Height = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        else if (Classes.IconGallery.IsFluentGlyph(item.IconGlyph))
        {
            iconElement = new FontIcon
            {
                Glyph = item.IconGlyph,
                FontSize = iconSize - 4,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (ParseIconColor(item.IconColor) is SolidColorBrush brush1)
                ((FontIcon)iconElement).Foreground = brush1;
        }
        else
        {
            iconElement = new TextBlock
            {
                Text = item.IconGlyph,
                FontSize = iconSize - 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            if (ParseIconColor(item.IconColor) is SolidColorBrush brush2)
                ((TextBlock)iconElement).Foreground = brush2;
        }

        var nameText = new TextBlock
        {
            Text = item.Name,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            MaxWidth = GetActiveIconCellWidth() - 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

        iconElement.SetValue(Grid.RowProperty, 0);
        iconElement.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        iconElement.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.Children.Add(iconElement);

        nameText.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(nameText, 1);
        content.Children.Add(nameText);

        var tile = new Button
        {
            Width = GetActiveIconCellWidth(),
            Height = GetActiveIconCellHeight(),
            Padding = new Thickness(4),
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content,
            Tag = item,
        };
        tile.Click += IconTile_Click;
        tile.RightTapped += IconTile_RightTapped;
        return tile;
    }

    private void IconTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LauncherItem item && !item.IsGroup)
            LaunchItem(item);
    }

    private void IconTile_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LauncherItem item && !item.IsGroup)
        {
            var flyout = new MenuFlyout();
            var editItem = new MenuFlyoutItem { Text = "Edit" };
            editItem.Click += (_, _) => EditItem(item);
            flyout.Items.Add(editItem);
            flyout.ShowAt(btn);
        }
    }

    private void RebuildColumnsPanel()
    {
        _columnLists = BuildColumnLists();
        _loadedIconChildLists.Clear();
        _syntheticGroups.Clear();
        if (IsIconMode)
            WrapUngroupedItemsIntoSyntheticGroups();

        if (RootGrid.Resources["IconItemTemplateSelector"] is FlyoutItemTemplateSelector iconSelector)
            iconSelector.SyntheticGroups = _syntheticGroups;
        if (RootGrid.Resources["SmallIconItemTemplateSelector"] is FlyoutItemTemplateSelector smallIconSelector)
            smallIconSelector.SyntheticGroups = _syntheticGroups;

        ColumnsPanel.Children.Clear();
        UpdateResizeGripVisibility();

        // Show/hide launcher title at the top
        if (_launcher.ShowTitle)
        {
            LauncherTitle.Text = _launcher.Name;
            LauncherTitle.Visibility = Visibility.Visible;
        }
        else
        {
            LauncherTitle.Visibility = Visibility.Collapsed;
        }

        for (int columnIndex = 0; columnIndex < _columnLists.Count; columnIndex++)
        {
            var lv = CreateColumnListView(columnIndex);
            lv.ItemsSource = _columnLists[columnIndex];
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
            // Refresh composite tray icon (mode 13) since it derives from item icons
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            if (launcher?.TrayIconMode == TrayIconModes.Composite)
                MainWindow.Current?.UpdateTrayIcon(launcher);
        }
        else
        {
            foreach (var fw in _instances.Values)
            {
                fw._lastItemsHash = -1;
                fw.RebuildItemsIfNeeded();
            }
            // Refresh all composite tray icons
            foreach (var launcher in SettingsManager.Current.Launchers)
            {
                if (launcher.TrayIconMode == TrayIconModes.Composite)
                    MainWindow.Current?.UpdateTrayIcon(launcher);
            }
        }
    }

    /// <summary>Invalidates all launcher flyout instances.</summary>
    internal static void InvalidateAllItems() => InvalidateItems(null);

    /// <summary>Parses a hex color string to a SolidColorBrush, or null if empty/invalid.</summary>
    private static SolidColorBrush? ParseIconColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
            }
        }
        catch { /* fall through */ }
        return null;
    }



    // ── Positioning ─────────────────────────────────────────────────

    private FlyoutPlacement CalculatePlacement(int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        int flyoutWidth = (int)Math.Ceiling(GetFlyoutWidth() * scale);
        int flyoutHeight = (int)Math.Ceiling(MeasureContentHeight() * scale);
        int gap = Math.Max(4, (int)Math.Round(8 * scale));
        int slideDistance = Math.Max(18, (int)Math.Round(SlideDistanceDip * scale));

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

        var edge = nearTop ? FlyoutEntranceEdge.Top : FlyoutEntranceEdge.Bottom;
        int startTop = edge == FlyoutEntranceEdge.Top ? top - slideDistance : top + slideDistance;
        return new FlyoutPlacement(left, top, startTop, flyoutWidth, flyoutHeight, edge);
    }

    private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsIconMode || sender is not ResizeGrip grip)
            return;

        _isResizingIconWidth = true;
        _resizeFromLeft = ReferenceEquals(sender, _leftResizeGrip);
        _resizeChangedSetting = false;
        _resizeStartIconsPerRow = GetIconModeIconsPerRow();
        GetCursorPos(out var pt);
        _resizeStartCursorX = pt.X;
        _animationVersion++;
        _isHiding = false;
        _isShowing = false;

        grip.CapturePointer(e.Pointer);
        grip.SetResizeCursorActive(true);
        e.Handled = true;
    }

    private void ResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingIconWidth)
            return;

        GetCursorPos(out var pt);
        int dragDistance = _resizeFromLeft ? _resizeStartCursorX - pt.X : pt.X - _resizeStartCursorX;
        int stepWidth = GetIconResizeStepWidth();
        int deltaSteps = (int)Math.Round((double)dragDistance / stepWidth, MidpointRounding.AwayFromZero);
        int targetIconsPerRow = Launcher.ClampIconModeIconsPerRow(_resizeStartIconsPerRow + deltaSteps);

        ApplyIconModeResize(targetIconsPerRow, keepRightEdge: _resizeFromLeft);
        e.Handled = true;
    }

    private void ResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompleteIconResize(sender as ResizeGrip);
        e.Handled = true;
    }

    private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CompleteIconResize(sender as ResizeGrip);
    }

    private void CompleteIconResize(ResizeGrip? grip)
    {
        if (!_isResizingIconWidth) return;

        grip?.ReleasePointerCaptures();

        _isResizingIconWidth = false;
        _leftResizeGrip.SetResizeCursorActive(false);
        _rightResizeGrip.SetResizeCursorActive(false);

        if (_resizeChangedSetting)
            SettingsManager.SaveSettings();
    }

    private void ApplyIconModeResize(int iconsPerRow, bool keepRightEdge)
    {
        int clamped = Launcher.ClampIconModeIconsPerRow(iconsPerRow);
        if (_launcher.IconModeIconsPerRow == clamped)
            return;

        _launcher.IconModeIconsPerRow = clamped;
        _resizeChangedSetting = true;
        UpdateFlyoutLayoutInPlace();
        _lastItemsHash = ComputeItemsHash(_launcher);
        ResizeWindowToCurrentContent(keepRightEdge);
    }

    private void UpdateFlyoutLayoutInPlace()
    {
        foreach (var columnListView in ColumnsPanel.Children.OfType<ListView>())
        {
            DisableListViewTransitions(columnListView);

            if (IsIconMode && columnListView.Tag is int columnIndex && columnIndex >= 0 && columnIndex < _columnLists.Count)
                columnListView.Width = GetIconColumnWidth(_columnLists[columnIndex]);
        }

        if (!IsIconMode)
            return;

        foreach (var childListView in _loadedIconChildLists.ToList())
        {
            DisableListViewTransitions(childListView);

            if (childListView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
                wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();

            if (childListView.Tag is LauncherItem group)
            {
                int widestRowIcons = Math.Max(1, Math.Min(GetIconModeIconsPerRow(), group.Children.Count));
                childListView.Width = widestRowIcons * GetActiveIconCellWidth();
                childListView.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }
    }

    private void ResizeWindowToCurrentContent(bool keepRightEdge)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        GetWindowRect(_hwnd, out var rect);
        double dpiScale = GetDpiForWindow(_hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;

        int newWidth = (int)Math.Ceiling(GetFlyoutWidth() * dpiScale);
        int newHeight = (int)Math.Ceiling(MeasureContentHeight() * dpiScale);
        int left = keepRightEdge ? rect.Right - newWidth : rect.Left;
        int top = _lastEntranceEdge == FlyoutEntranceEdge.Top ? rect.Top : rect.Bottom - newHeight;

        var centerPoint = new POINT
        {
            X = rect.Left + ((rect.Right - rect.Left) / 2),
            Y = rect.Top + ((rect.Bottom - rect.Top) / 2)
        };
        IntPtr hMonitor = MonitorFromPoint(centerPoint, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        if (left < workArea.Left) left = workArea.Left;
        if (left + newWidth > workArea.Right) left = workArea.Right - newWidth;
        if (top < workArea.Top) top = workArea.Top;
        if (top + newHeight > workArea.Bottom) top = workArea.Bottom - newHeight;

        SetWindowPos(_hwnd, 0, left, top, newWidth, newHeight,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    private void IconGroupChildList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        _loadedIconChildLists.Add(listView);
        listView.Unloaded -= IconGroupChildList_Unloaded;
        listView.Unloaded += IconGroupChildList_Unloaded;

        DisableListViewTransitions(listView);
        ScrollViewer.SetVerticalScrollBarVisibility(listView, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(listView, ScrollBarVisibility.Disabled);

        if (listView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();

        if (listView.Tag is LauncherItem group)
        {
            int widestRowIcons = Math.Max(1, Math.Min(GetIconModeIconsPerRow(), group.Children.Count));
            listView.Width = widestRowIcons * GetActiveIconCellWidth();
            listView.HorizontalAlignment = HorizontalAlignment.Center;
        }
    }

    private void IconGroupChildList_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
            _loadedIconChildLists.Remove(listView);
    }

    private void ListGroupChildList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
            DisableListViewTransitions(listView);
    }

    private void ColumnListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListView listView || listView.Tag is not int columnIndex)
            return;

        if (e.Items.FirstOrDefault() is not LauncherItem item)
            return;

        if (_syntheticGroups.Contains(item))
        {
            e.Cancel = true;
            return;
        }

        _dragItem = item;
        _dragSourceCollection = _columnLists[columnIndex];
        e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void ColumnListView_DragOver(object sender, DragEventArgs e)
    {
        if (_dragItem == null || _dragSourceCollection == null || sender is not ListView listView)
            return;

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        int dropIndex = GetDropIndex(listView, e);
        ShowInsertionIndicator(listView, dropIndex);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;

        if (dropIndex < listView.Items.Count && listView.Items[dropIndex] is LauncherItem targetItem)
            e.DragUIOverride.Caption = $"Move above {GetItemDisplayName(targetItem)}";
        else
            e.DragUIOverride.Caption = "Move to end";

        e.Handled = true;
    }

    private void ColumnListView_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
    }

    private void ColumnListView_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        if (_dragItem == null || _dragSourceCollection == null || sender is not ListView listView || listView.Tag is not int columnIndex)
            return;

        var targetColumn = _columnLists[columnIndex];
        int dropIndex = GetDropIndex(listView, e);

        int originalIndex = _dragSourceCollection == targetColumn ? _dragSourceCollection.IndexOf(_dragItem) : -1;
        _dragSourceCollection.Remove(_dragItem);
        if (originalIndex >= 0 && originalIndex < dropIndex)
            dropIndex--;

        dropIndex = Math.Clamp(dropIndex, 0, targetColumn.Count);
        targetColumn.Insert(dropIndex, _dragItem);

        PersistFlyoutReorder();
        ClearDragState();
        e.Handled = true;
    }

    private void ColumnListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        ClearDragState();
    }

    private void GroupChildList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (e.Items.FirstOrDefault() is not LauncherItem item)
            return;

        _dragItem = item;
        _dragSourceCollection = group.Children;
        e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void GroupChildList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (_dragItem == null || _dragSourceCollection == null)
            return;

        if (_dragItem.IsGroup || _dragItem.IsColumnBreak)
        {
            e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        int dropIndex = GetDropIndex(listView, e);
        ShowInsertionIndicator(listView, dropIndex);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;

        if (dropIndex < group.Children.Count)
        {
            bool isGrid = listView.ItemsPanelRoot is ItemsWrapGrid;
            e.DragUIOverride.Caption = isGrid
                ? $"Move before {GetItemDisplayName(group.Children[dropIndex])}"
                : $"Move above {GetItemDisplayName(group.Children[dropIndex])}";
        }
        else if (group.Children.Count == 0)
        {
            e.DragUIOverride.Caption = $"Move into {GetItemDisplayName(group)}";
        }
        else
        {
            e.DragUIOverride.Caption = $"Move to end of {GetItemDisplayName(group)}";
        }

        e.Handled = true;
    }

    private void GroupChildList_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
    }

    private void GroupChildList_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (_dragItem == null || _dragSourceCollection == null || _dragItem.IsGroup || _dragItem.IsColumnBreak)
            return;

        int dropIndex = GetDropIndex(listView, e);
        int originalIndex = _dragSourceCollection == group.Children ? _dragSourceCollection.IndexOf(_dragItem) : -1;
        _dragSourceCollection.Remove(_dragItem);
        if (originalIndex >= 0 && originalIndex < dropIndex)
            dropIndex--;

        dropIndex = Math.Clamp(dropIndex, 0, group.Children.Count);
        group.Children.Insert(dropIndex, _dragItem);

        PersistFlyoutReorder();
        ClearDragState();
        e.Handled = true;
    }

    private void GroupChildList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        ClearDragState();
    }

    private void PersistFlyoutReorder()
    {
        SyncColumnsToFlatList();
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyItemsChanged();
        LauncherItemsPage.NotifyItemsChanged(_launcher.Id);

        if (_launcher.TrayIconMode == TrayIconModes.Composite)
            MainWindow.Current?.UpdateTrayIcon(_launcher);

        _lastItemsHash = ComputeItemsHash(_launcher);
        UpdateFlyoutLayoutInPlace();
    }

    private void ClearDragState()
    {
        _dragItem = null;
        _dragSourceCollection = null;
    }

    private static string GetItemDisplayName(LauncherItem item)
    {
        if (item.IsGroup && string.IsNullOrWhiteSpace(item.Name))
            return "section";

        return string.IsNullOrWhiteSpace(item.Name) ? "item" : item.Name;
    }

    private void ShowInsertionIndicator(ListView listView, int dropIndex)
    {
        ClearInsertionIndicator();

        if (listView.Items.Count == 0)
        {
            _lastIndicatorListView = listView;
            _lastIndicatorListBorderBrush = listView.BorderBrush;
            _lastIndicatorListBorderThickness = listView.BorderThickness;
            listView.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            listView.BorderThickness = new Thickness(2);
            return;
        }

        bool isGrid = listView.ItemsPanelRoot is ItemsWrapGrid;
        int targetIndex = dropIndex < listView.Items.Count ? dropIndex : listView.Items.Count - 1;
        if (targetIndex < 0)
            return;

        if (listView.ContainerFromIndex(targetIndex) is not ListViewItem container)
            return;

        _lastIndicatorContainer = container;
        _lastIndicatorContainerBorderBrush = container.BorderBrush;
        _lastIndicatorContainerBorderThickness = container.BorderThickness;
        _lastIndicatorContainerPadding = container.Padding;
        _lastIndicatorContainerMargin = container.Margin;
        container.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        if (isGrid)
        {
            if (dropIndex < listView.Items.Count)
            {
                container.BorderThickness = new Thickness(3, 0, 0, 0);
                container.Padding = new Thickness(-3, 0, 0, 0);
            }
            else
            {
                container.BorderThickness = new Thickness(0, 0, 3, 0);
                container.Padding = new Thickness(0, 0, -3, 0);
            }
        }
        else
        {
            bool insertBefore = dropIndex < listView.Items.Count;
            var padding = _lastIndicatorContainerPadding;

            if (insertBefore)
            {
                container.BorderThickness = new Thickness(0, 2, 0, 0);
                container.Padding = new Thickness(
                    padding.Left,
                    Math.Max(0, padding.Top - 2),
                    padding.Right,
                    padding.Bottom);
            }
            else
            {
                container.BorderThickness = new Thickness(0, 0, 0, 2);
                container.Padding = new Thickness(
                    padding.Left,
                    padding.Top,
                    padding.Right,
                    Math.Max(0, padding.Bottom - 2));
            }

            container.Margin = _lastIndicatorContainerMargin;
        }
    }

    private void ClearInsertionIndicator()
    {
        if (_lastIndicatorContainer != null)
        {
            _lastIndicatorContainer.BorderBrush = _lastIndicatorContainerBorderBrush;
            _lastIndicatorContainer.BorderThickness = _lastIndicatorContainerBorderThickness;
            _lastIndicatorContainer.Padding = _lastIndicatorContainerPadding;
            _lastIndicatorContainer.Margin = _lastIndicatorContainerMargin;
            _lastIndicatorContainer = null;
            _lastIndicatorContainerBorderBrush = null;
            _lastIndicatorContainerBorderThickness = new Thickness(0);
            _lastIndicatorContainerPadding = new Thickness(0);
            _lastIndicatorContainerMargin = new Thickness(0);
        }

        if (_lastIndicatorListView != null)
        {
            _lastIndicatorListView.BorderBrush = _lastIndicatorListBorderBrush;
            _lastIndicatorListView.BorderThickness = _lastIndicatorListBorderThickness;
            _lastIndicatorListView = null;
            _lastIndicatorListBorderBrush = null;
            _lastIndicatorListBorderThickness = new Thickness(0);
        }
    }

    private static int GetDropIndex(ListView listView, DragEventArgs e)
    {
        if (listView.ItemsPanelRoot is ItemsWrapGrid)
            return GetDropIndexGrid(listView, e);

        var position = e.GetPosition(listView);
        for (int index = 0; index < listView.Items.Count; index++)
        {
            if (listView.ContainerFromIndex(index) is not ListViewItem container)
                continue;

            var transform = container.TransformToVisual(listView);
            var point = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
            if (position.Y < point.Y + (container.ActualHeight / 2))
                return index;
        }

        return listView.Items.Count;
    }

    private static int GetDropIndexGrid(ListView listView, DragEventArgs e)
    {
        var position = e.GetPosition(listView);
        int count = listView.Items.Count;
        if (count == 0)
            return 0;

        int bestIndex = count;
        double bestDistance = double.MaxValue;

        for (int index = 0; index < count; index++)
        {
            if (listView.ContainerFromIndex(index) is not ListViewItem container)
                continue;

            var transform = container.TransformToVisual(listView);
            var point = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));

            double top = point.Y;
            double bottom = top + container.ActualHeight;
            double left = point.X;
            double midX = left + (container.ActualWidth / 2);

            if (position.Y >= top && position.Y < bottom)
            {
                if (position.X < midX)
                    return index;

                bestIndex = index + 1;
                bestDistance = 0;
            }
            else if (bestDistance > 0)
            {
                double verticalDistance = position.Y < top ? top - position.Y : position.Y - bottom;
                if (verticalDistance < bestDistance)
                {
                    bestDistance = verticalDistance;
                    bestIndex = position.X < midX ? index : index + 1;
                }
            }
        }

        return Math.Min(bestIndex, count);
    }

    private sealed class ResizeGrip : Grid
    {
        private bool _forceResizeCursor;

        public ResizeGrip()
        {
            PointerEntered += ResizeGrip_PointerEntered;
            PointerExited += ResizeGrip_PointerExited;
            PointerCaptureLost += ResizeGrip_PointerCaptureLost;
        }

        public void SetResizeCursorActive(bool active)
        {
            _forceResizeCursor = active;
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                active ? Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast : Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

        private void ResizeGrip_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        }

        private void ResizeGrip_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_forceResizeCursor)
                return;

            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

        private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _forceResizeCursor = false;
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void ItemsListControl_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LauncherItem item) return;
        if (item.IsGroup) return;
        LaunchItem(item);
    }

    private void LaunchItem(LauncherItem item)
    {
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
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"shell:AppsFolder\\{item.Path}",
                    UseShellExecute = false
                });
            }
            else if (item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
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

    private void ContextEditLauncherSettings_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _lastDismissed = DateTime.UtcNow;
        if (_owner != null)
        {
            SettingsWindow.ShowInstance(_owner);
            var sw = SettingsWindow.GetCurrent();
            sw?.DispatcherQueue.TryEnqueue(() => sw.NavigateToLauncherSettings(_launcher));
        }
    }

    private void ContextEditLauncherItems_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _lastDismissed = DateTime.UtcNow;
        if (_owner != null)
        {
            SettingsWindow.ShowInstance(_owner);
            var sw = SettingsWindow.GetCurrent();
            sw?.DispatcherQueue.TryEnqueue(() => sw.NavigateToLauncherItems(_launcher));
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
        if (IsIconMode)
            return MeasureIconModeHeight();

        // Calculate height arithmetically instead of calling UpdateLayout()/Measure()
        // on a potentially hidden window. Forcing a XAML layout pass on a window hidden
        // via ShowWindow(SW_HIDE) while another WinUI 3 window is active causes a fatal
        // ExecutionEngineException in Microsoft.WinUI.dll.
        //
        // Each ListViewItem container: MinHeight=0, Padding="8,6" → 12px vertical padding.
        // Regular item content: Icon 20px tall → total ~32px
        // Group header content: NavigationViewItemHeader ~40px
        // Heading content:      FontSize=11 (~15px) + Margin top 4 → total ~31px
        const double itemHeight = 32;
        const double groupHeight = 40;
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
        double titleHeight = _launcher.ShowTitle ? 24 : 0;
        double maxContentHeight = GetWorkAreaHeightDips() - 16; // 16 = gap from taskbar edges
        _lastMeasuredHeight = Math.Clamp(maxColumnHeight + titleHeight + 2, GetMinimumFlyoutHeight(), maxContentHeight);
        return _lastMeasuredHeight;
    }

    private double MeasureIconModeHeight()
    {
        const double listPadding = 12;
        int iconsPerRow = GetIconModeIconsPerRow();
        int cellHeight = GetActiveIconCellHeight();
        double groupHeight = GetActiveGroupHeaderHeight();

        var items = _launcher.Items;
        if (items == null) return _lastMeasuredHeight;

        double maxColumnHeight = 0;
        double currentColumnHeight = listPadding;
        int pendingIcons = 0;

        foreach (var item in items)
        {
            if (item.IsColumnBreak)
            {
                if (pendingIcons > 0)
                {
                    int rows = (pendingIcons + iconsPerRow - 1) / iconsPerRow;
                    currentColumnHeight += rows * cellHeight;
                    pendingIcons = 0;
                }
                maxColumnHeight = Math.Max(maxColumnHeight, currentColumnHeight);
                currentColumnHeight = listPadding;
                continue;
            }

            if (item.IsGroup)
            {
                if (pendingIcons > 0)
                {
                    int rows = (pendingIcons + iconsPerRow - 1) / iconsPerRow;
                    currentColumnHeight += rows * cellHeight;
                    pendingIcons = 0;
                }
                currentColumnHeight += groupHeight;
                foreach (var _ in item.Children)
                    pendingIcons++;
            }
            else
            {
                pendingIcons++;
            }
        }

        if (pendingIcons > 0)
        {
            int rows = (pendingIcons + iconsPerRow - 1) / iconsPerRow;
            currentColumnHeight += rows * cellHeight;
        }

        maxColumnHeight = Math.Max(maxColumnHeight, currentColumnHeight);
        double titleHeight = _launcher.ShowTitle ? 24 : 0;
        double maxContentHeight = GetWorkAreaHeightDips() - 16;
        _lastMeasuredHeight = Math.Clamp(maxColumnHeight + titleHeight + 2, GetMinimumFlyoutHeight(), maxContentHeight);
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