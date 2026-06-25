using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Controls;
using LittleLauncher.Models;
using LittleLauncher.Services;
using LittleLauncher.ViewModels;
using LittleLauncher.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Xml.Serialization;
using global::Windows.Storage.Pickers;
using WinRT.Interop;
using Image = Microsoft.UI.Xaml.Controls.Image;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Pages;

/// <summary>
/// Selects the correct DataTemplate based on item type: group, heading, or launchable item.
/// </summary>
public class LauncherItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? LauncherItemTemplate { get; set; }
    public DataTemplate? GroupItemTemplate { get; set; }
    public DataTemplate? SyntheticGroupTemplate { get; set; }
    public HashSet<LauncherItem>? SyntheticGroups { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return SelectTemplateForItem(item);
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateForItem(item);
    }

    private DataTemplate? SelectTemplateForItem(object item)
    {
        if (item is LauncherItem li)
        {
            if (li.IsGroup && SyntheticGroups?.Contains(li) == true && SyntheticGroupTemplate != null)
                return SyntheticGroupTemplate;
            if (li.IsGroup) return GroupItemTemplate;
        }
        return LauncherItemTemplate;
    }
}

public partial class LauncherItemsPage : Page
{
    /// <summary>
    /// The launcher to edit. Set by LaunchersPage (or FlyoutWindow.EditItem) before navigating here.
    /// Falls back to the first launcher when null.
    /// </summary>
    internal static Launcher? TargetLauncher { get; set; }

    /// <summary>The items collection of the currently targeted launcher.</summary>
    private static System.Collections.ObjectModel.ObservableCollection<LauncherItem> CurrentItems =>
        TargetLauncher?.Items
        ?? SettingsManager.Current.Launchers.FirstOrDefault()?.Items
        ?? [];

    /// <summary>
    /// When set, the edit dialog for this item opens automatically after the page loads.
    /// </summary>
    internal static LauncherItem? PendingEditItem { get; set; }

    // -- Cross-list drag-and-drop tracking --
    private LauncherItem? _dragItem;
    private ObservableCollection<LauncherItem>? _dragSourceCollection;
    private Control? _lastIndicatorContainer;
    private ListViewBase? _lastIndicatorListView;
    private bool _isReadOnly;
    private readonly List<Border> _newColumnDropZones = [];

    /// <summary>Column padding added to the total columns width calculation.</summary>
    private const int ColumnLayoutPadding = 80;
    private const int ColumnSpacing = 12;
    private const int ListModeColumnWidth = 280;
    private const int DefaultIconModeColumnWidth = 265;
    private const int IconModeTileOuterWidth = 84;
    private const int IconModeTileOuterHeight = 86;
    private const int IconModeColumnChromeWidth = DefaultIconModeColumnWidth - (IconModeTileOuterWidth * Launcher.DefaultIconModeIconsPerRow);

    // -- Cached resources (created once, reused on every pointer/drag event) --
    private static readonly Microsoft.UI.Input.InputSystemCursor _sizeAllCursor =
        Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
    private static readonly Microsoft.UI.Input.InputSystemCursor _arrowCursor =
        Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    private Microsoft.UI.Xaml.Media.Brush? _accentBrushCache;
    private Microsoft.UI.Xaml.Media.Brush AccentBrush =>
        _accentBrushCache ??= Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var b)
            ? (Microsoft.UI.Xaml.Media.Brush)b
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);

    // -- Column-based rendering state --
    private List<ObservableCollection<LauncherItem>> _columnLists = [];
    private readonly HashSet<LauncherItem> _syntheticGroups = [];

    private static int GetIconModeIconsPerRow() =>
        Launcher.ClampIconModeIconsPerRow(TargetLauncher?.IconModeIconsPerRow ?? Launcher.DefaultIconModeIconsPerRow);

    private static int GetIconModeColumnWidth() => IconModeColumnChromeWidth + (GetIconModeIconsPerRow() * IconModeTileOuterWidth);

    private static int GetIconModeGroupContentWidth(LauncherItem group)
    {
        int visibleIcons = Math.Clamp(group.Children.Count, 1, GetIconModeIconsPerRow());
        return visibleIcons * IconModeTileOuterWidth;
    }

    private static int GetIconModeTopLevelSpan(LauncherItem item)
    {
        if (!item.IsGroup)
            return 1;

        return Math.Clamp(item.Children.Count, 1, GetIconModeIconsPerRow());
    }

    private int GetIconModeTopLevelRowSpan(LauncherItem item)
    {
        if (!item.IsGroup)
            return 1;

        int childRows = Math.Max(1, (item.Children.Count + GetIconModeIconsPerRow() - 1) / GetIconModeIconsPerRow());
        return _syntheticGroups.Contains(item) ? childRows : childRows + 1;
    }

    private static bool IsGridItemsPanel(Panel? panel) =>
        panel is ItemsWrapGrid or PackedIconPanel;

    public LauncherItemsPage()
    {
        InitializeComponent();
        RebuildColumns();
        Loaded += LauncherItemsPage_Loaded;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        string launcherName = TargetLauncher?.Name ?? "Default";
        LauncherNameCrumb.Text = launcherName;

        // Shared subscriber launchers are read-only — items come from the owner
        _isReadOnly = TargetLauncher is { IsShared: true, IsSharedOwner: false };
        AddButtonsToolbar.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
        ImportButton.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
        if (_isReadOnly)
            PageSubtitle.Text = "These items are managed by the shared launcher owner and cannot be edited here.";
        else
            PageSubtitle.Text = "Add, edit, or remove apps and websites from your little launcher. Drag items to reorder.";

        RebuildColumns();
        ColumnsScrollViewer.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ColumnsScrollViewer_PointerWheelChanged),
            true);
    }

    private void ColumnsScrollViewer_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(ColumnsScrollViewer).Properties;
        if (props.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(global::Windows.System.VirtualKeyModifiers.Shift))
        {
            var delta = props.IsHorizontalMouseWheel ? props.MouseWheelDelta : props.MouseWheelDelta;
            ColumnsScrollViewer.ChangeView(ColumnsScrollViewer.HorizontalOffset - delta, null, null, true);
            e.Handled = true;
        }
    }

    private void BackToLayouts_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(LaunchersPage));
    }

    private async void LauncherItemsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (PendingEditItem is { } item)
        {
            PendingEditItem = null;
            if (item.IsGroup)
                await ShowGroupDialog(item);
            else
                await ShowItemDialog(item);
        }
    }

    /// <summary>
    /// Splits CurrentItems at column break sentinels into separate ObservableCollections
    /// and renders each as a ListView column inside ColumnsPanel.
    /// </summary>
    private void RebuildColumns()
    {
        _columnLists = BuildColumnLists();
        _syntheticGroups.Clear();

        // Auto-prune empty columns (e.g. after removing the last item via menu)
        if (AutoRemoveEmptyColumns())
        {
            SyncColumnsToFlatList();
            SettingsManager.SaveSettings();
        }

        // In icon mode, wrap consecutive ungrouped items into synthetic groups
        // so they use the wrapping grid layout
        bool isIconMode = LauncherViewModes.IsIconMode(TargetLauncher?.ViewMode ?? LauncherViewModes.Icon);
        if (isIconMode)
            WrapUngroupedItemsIntoSyntheticGroups();

        ColumnsPanel.Children.Clear();
        ColumnsPanel.ColumnDefinitions.Clear();
        ColumnsPanel.RowDefinitions.Clear();

        // Set up column definitions in the ColumnsPanel Grid.
        // Layout: [dropZone0] [col0] [dropZone1] [col1] ... [dropZoneN]
        // Grid columns: Auto, Fixed, Auto, Fixed, ..., Auto
        int colFixedWidth = isIconMode ? GetIconModeColumnWidth() : ListModeColumnWidth;
        for (int c = 0; c < _columnLists.Count; c++)
        {
            // Drop zone column (between previous column and this one, or before first)
            ColumnsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // Content column
            ColumnsPanel.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(colFixedWidth)
            });
        }
        // Final drop zone column (after the last content column)
        ColumnsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Two rows: Auto for header, Star for the ListView
        ColumnsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnsPanel.ColumnSpacing = 0;

        // Size to fit columns: single column ~360, grows with more columns
        RootGrid.MaxWidth = CalculateTotalColumnsWidth();

        _newColumnDropZones.Clear();

        for (int colIdx = 0; colIdx < _columnLists.Count; colIdx++)
        {
            int contentGridCol = colIdx * 2 + 1; // skip the drop zone column before it
            var colItems = _columnLists[colIdx];

            // Column header (shown when multiple columns exist)
            if (_columnLists.Count > 1)
            {
                var headerText = new TextBlock
                {
                    Text = $"Column {colIdx + 1}",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 13,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                Grid.SetRow(headerText, 0);
                Grid.SetColumn(headerText, contentGridCol);
                ColumnsPanel.Children.Add(headerText);
            }

            // ListView for this column
            ListViewBase lv = isIconMode
                ? new GridView()
                : new ListView();

            lv.CanDragItems = !_isReadOnly;
            lv.AllowDrop = !_isReadOnly;
            lv.SelectionMode = ListViewSelectionMode.None;
            lv.Tag = colIdx;
            if (isIconMode)
            {
                var baseSelector = (LauncherItemTemplateSelector)Resources["IconModeTemplateSelector"];
                lv.ItemTemplateSelector = new LauncherItemTemplateSelector
                {
                    LauncherItemTemplate = baseSelector.LauncherItemTemplate,
                    GroupItemTemplate = baseSelector.GroupItemTemplate,
                    SyntheticGroupTemplate = (DataTemplate)Resources["IconModeSyntheticGroupTemplate"],
                    SyntheticGroups = _syntheticGroups,
                };
                lv.ItemContainerStyle = CreateIconModeGridContainerStyle();
                lv.ItemsPanel = CreateTopLevelIconModeItemsPanel();
                lv.Loaded += IconModeColumnList_Loaded;
                lv.ContainerContentChanging += IconModeColumn_ContainerContentChanging;
            }
            else
            {
                lv.ItemTemplateSelector = (DataTemplateSelector)Resources["ItemTemplateSelector"];
                lv.ItemContainerStyle = CreateItemContainerStyle();
            }
            lv.ItemsSource = colItems;
            lv.DragItemsStarting += ColumnListView_DragItemsStarting;
            lv.DragOver += ColumnListView_DragOver;
            lv.DragLeave += ColumnListView_DragLeave;
            lv.Drop += ColumnListView_Drop;
            lv.DragItemsCompleted += ColumnListView_DragItemsCompleted;

            Grid.SetRow(lv, 1);
            Grid.SetColumn(lv, contentGridCol);
            ColumnsPanel.Children.Add(lv);
        }

        // Inter-column "new column" drop zones (between and after every column)
        if (!_isReadOnly)
        {
            // Drop zones at positions 0, 1, ..., _columnLists.Count
            // Grid columns: 0, 2, 4, ..., _columnLists.Count * 2
            for (int z = 0; z <= _columnLists.Count; z++)
            {
                int gridCol = z * 2; // drop zone grid columns are 0, 2, 4, ...
                var zone = new Border
                {
                    AllowDrop = true,
                    MinWidth = 48,
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = AccentBrush,
                    BorderThickness = new Thickness(0),
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                    Visibility = Visibility.Collapsed,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Tag = z, // insert position in _columnLists
                    Child = new FontIcon
                    {
                        Glyph = "\uE710",
                        FontSize = 14,
                        Opacity = 0.5,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                };
                zone.DragOver += NewColumnZone_DragOver;
                zone.DragLeave += NewColumnZone_DragLeave;
                zone.Drop += NewColumnZone_Drop;
                Grid.SetRow(zone, 0);
                Grid.SetRowSpan(zone, 2);
                Grid.SetColumn(zone, gridCol);
                ColumnsPanel.Children.Add(zone);
                _newColumnDropZones.Add(zone);
            }
        }
    }

    private static Style CreateItemContainerStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    private static Style CreateIconModeContainerStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(ListViewItem.MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(ListViewItem.MinHeightProperty, 0.0));
        style.Setters.Add(new Setter(ListViewItem.HorizontalAlignmentProperty, HorizontalAlignment.Left));
        style.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        return style;
    }

    private static Style CreateIconModeGridContainerStyle()
    {
        var style = new Style(typeof(GridViewItem));
        style.Setters.Add(new Setter(GridViewItem.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(GridViewItem.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(GridViewItem.MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(GridViewItem.MinHeightProperty, 0.0));
        style.Setters.Add(new Setter(GridViewItem.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(GridViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    private static ItemsPanelTemplate CreateWrapGridItemsPanel()
    {
        return CreateWrapGridItemsPanel(GetIconModeIconsPerRow());
    }

    private static ItemsPanelTemplate CreateWrapGridItemsPanel(int iconsPerRow)
    {
        var xaml = "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                   $"<ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='{iconsPerRow}'/>" +
                   "</ItemsPanelTemplate>";
        return (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private ItemsPanelTemplate CreateTopLevelIconModeItemsPanel()
    {
        return (ItemsPanelTemplate)Resources["TopLevelIconModeItemsPanel"];
    }

    private void IconModeWrapList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        listView.ItemsPanel = CreateWrapGridItemsPanel();
        ApplyIconModeGroupListLayout(listView);

        if (listView.Tag is LauncherItem group && _syntheticGroups.Contains(group))
        {
            VariableSizedWrapGrid.SetColumnSpan(listView, GetIconModeTopLevelSpan(group));
            VariableSizedWrapGrid.SetRowSpan(listView, GetIconModeTopLevelRowSpan(group));
        }
    }

    private void IconModeColumnList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
            return;

        ApplyTopLevelIconModeSpans(listView);
    }

    private void IconModeColumn_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not LauncherItem item || args.ItemContainer is not Control container)
            return;

        if (sender.ItemsPanelRoot is PackedIconPanel wrapGrid)
        {
            wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();
            wrapGrid.ItemWidth = IconModeTileOuterWidth;
            wrapGrid.ItemHeight = IconModeTileOuterHeight;
            wrapGrid.InvalidateMeasure();
        }

        VariableSizedWrapGrid.SetColumnSpan(container, GetIconModeTopLevelSpan(item));
        VariableSizedWrapGrid.SetRowSpan(container, GetIconModeTopLevelRowSpan(item));
    }

    private static void ApplyIconModeGroupListLayout(ListView listView)
    {
        if (listView.Tag is not LauncherItem group)
            return;

        listView.Width = GetIconModeGroupContentWidth(group);
        listView.MaxWidth = GetIconModeIconsPerRow() * IconModeTileOuterWidth;
        listView.HorizontalAlignment = HorizontalAlignment.Center;
    }

    private static void ApplyIconModeGroupCardLayout(Border border)
    {
        if (!LauncherViewModes.IsIconMode(TargetLauncher?.ViewMode ?? LauncherViewModes.Icon)
            || border.DataContext is not LauncherItem group
            || !group.IsGroup)
        {
            return;
        }

        border.Width = GetIconModeGroupContentWidth(group);
        border.MaxWidth = GetIconModeColumnWidth();
        border.HorizontalAlignment = HorizontalAlignment.Center;
        VariableSizedWrapGrid.SetColumnSpan(border, GetIconModeTopLevelSpan(group));
        int childRows = Math.Max(1, (group.Children.Count + GetIconModeIconsPerRow() - 1) / GetIconModeIconsPerRow());
        VariableSizedWrapGrid.SetRowSpan(border, childRows + 1);
    }

    private void ApplyTopLevelIconModeSpans(ListViewBase listView)
    {
        if (listView.ItemsPanelRoot is not PackedIconPanel wrapGrid)
            return;

        wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();
        wrapGrid.ItemWidth = IconModeTileOuterWidth;
        wrapGrid.ItemHeight = IconModeTileOuterHeight;

        listView.UpdateLayout();

        for (int index = 0; index < listView.Items.Count; index++)
        {
            if (listView.Items[index] is not LauncherItem item || listView.ContainerFromIndex(index) is not Control container)
                continue;

            int columnSpan = GetIconModeTopLevelSpan(item);
            int rowSpan = GetIconModeTopLevelRowSpan(item);
            VariableSizedWrapGrid.SetColumnSpan(container, columnSpan);
            VariableSizedWrapGrid.SetRowSpan(container, rowSpan);

            if (index < wrapGrid.Children.Count && wrapGrid.Children[index] is UIElement panelChild)
            {
                VariableSizedWrapGrid.SetColumnSpan(panelChild, columnSpan);
                VariableSizedWrapGrid.SetRowSpan(panelChild, rowSpan);
            }
        }

        wrapGrid.InvalidateMeasure();
        wrapGrid.InvalidateArrange();
        listView.UpdateLayout();
    }

    /// <summary>
    /// Splits CurrentItems at IsColumnBreak sentinels into per-column lists.
    /// Column break items are excluded from the resulting lists.
    /// </summary>
    private static List<ObservableCollection<LauncherItem>> BuildColumnLists()
    {
        var columns = new List<ObservableCollection<LauncherItem>>();
        var current = new ObservableCollection<LauncherItem>();
        columns.Add(current);

        foreach (var item in CurrentItems)
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

    /// <summary>
    /// In icon mode, wraps consecutive ungrouped items into synthetic groups
    /// so they render in wrapping grids instead of one-per-row.
    /// </summary>
    private void WrapUngroupedItemsIntoSyntheticGroups()
    {
        for (int c = 0; c < _columnLists.Count; c++)
        {
            var col = _columnLists[c];
            var newCol = new ObservableCollection<LauncherItem>();
            LauncherItem? currentSynthetic = null;

            for (int i = 0; i < col.Count; i++)
            {
                var item = col[i];
                if (item.IsGroup)
                {
                    currentSynthetic = null;
                    newCol.Add(item);
                }
                else
                {
                    if (currentSynthetic == null)
                    {
                        currentSynthetic = new LauncherItem { Name = "", IsGroup = true, IsExpanded = true };
                        _syntheticGroups.Add(currentSynthetic);
                        newCol.Add(currentSynthetic);
                    }
                    currentSynthetic.Children.Add(item);
                }
            }

            _columnLists[c] = newCol;
        }
    }

    /// <summary>
    /// Writes the column lists back to the flat CurrentItems collection,
    /// inserting column break sentinels between columns.
    /// </summary>
    private void SyncColumnsToFlatList()
    {
        var items = CurrentItems;
        items.Clear();
        for (int c = 0; c < _columnLists.Count; c++)
        {
            if (c > 0)
                items.Add(LauncherItem.CreateColumnBreak());
            foreach (var item in _columnLists[c])
            {
                if (_syntheticGroups.Contains(item))
                {
                    // Unwrap synthetic group back to individual items
                    foreach (var child in item.Children)
                        items.Add(child);
                }
                else
                {
                    items.Add(item);
                }
            }
        }
    }

    /// <summary>
    /// Refreshes the settings page columns if the page is currently loaded.
    /// Called from FlyoutWindow after drag-reorder.
    /// </summary>
    internal static void NotifyItemsChanged(string? launcherId = null)
    {
        var settingsWindow = SettingsWindow.GetCurrent();
        if (settingsWindow?.CurrentPage is LauncherItemsPage page)
        {
            // Only refresh if the page is showing the affected launcher
            if (launcherId == null || TargetLauncher?.Id == launcherId)
                page.RebuildColumns();
        }
    }

    // -- Column ListView drag-and-drop handlers --

    private void ColumnListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is ListViewBase lv && lv.Tag is int colIdx
            && e.Items.FirstOrDefault() is LauncherItem item)
        {
            _dragItem = item;
            _dragSourceCollection = _columnLists[colIdx];
            e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            ShowNewColumnDropZones();
        }
    }

    private void ColumnListView_DragOver(object sender, DragEventArgs e)
    {
        if (_dragItem == null || _dragSourceCollection == null) return;
        if (sender is not ListViewBase lv || lv.Tag is not int colIdx) return;

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        var targetCol = _columnLists[colIdx];
        int dropIndex = GetDropIndex(lv, e);
        ShowInsertionIndicator(lv, dropIndex);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        if (dropIndex < targetCol.Count)
        {
            var targetItem = targetCol[dropIndex];
            bool isGrid = IsGridItemsPanel(lv.ItemsPanelRoot);
            e.DragUIOverride.Caption = isGrid
                ? $"Move before {targetItem.Name}"
                : $"Move above {targetItem.Name}";
        }
        else
        {
            e.DragUIOverride.Caption = "Move to end";
        }

        e.Handled = true;
    }

    private void ColumnListView_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
    }

    private void ColumnListView_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        HideNewColumnDropZones();
        if (_dragItem == null || _dragSourceCollection == null) return;
        if (sender is not ListViewBase lv || lv.Tag is not int colIdx) return;

        var targetCol = _columnLists[colIdx];
        int dropIndex = GetDropIndex(lv, e);

        // If reordering within the same column, adjust for the removal shift
        int originalIndex = _dragSourceCollection == targetCol ? _dragSourceCollection.IndexOf(_dragItem) : -1;
        _dragSourceCollection.Remove(_dragItem);
        if (originalIndex >= 0 && originalIndex < dropIndex)
            dropIndex--;
        if (dropIndex > targetCol.Count) dropIndex = targetCol.Count;
        targetCol.Insert(dropIndex, _dragItem);

        _dragItem = null;
        _dragSourceCollection = null;

        SyncColumnsToFlatList();
        bool columnsChanged = AutoRemoveEmptyColumns();
        if (columnsChanged)
        {
            SyncColumnsToFlatList();
            RebuildColumns();
        }
        SaveAndUpdateTaskbar();
        e.Handled = true;
    }

    private void ColumnListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        HideNewColumnDropZones();
        TopLevelDropZone.Visibility = Visibility.Collapsed;
        _dragItem = null;
        _dragSourceCollection = null;
    }

    private void GroupRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel groupRoot) return;

        // Disable editing controls inside groups for read-only subscribers
        if (_isReadOnly)
        {
            foreach (var child in groupRoot.Children)
            {
                if (child is StackPanel childPanel && childPanel.Tag as string == "GroupChildren")
                {
                    // Find and hide the "Add Item" button panel, disable child ListView drag
                    foreach (var gc in childPanel.Children)
                    {
                        if (gc is ListView lv)
                        {
                            lv.CanDragItems = false;
                            lv.AllowDrop = false;
                        }
                        else if (gc is StackPanel addBtnPanel && addBtnPanel.Orientation == Orientation.Horizontal)
                        {
                            addBtnPanel.Visibility = Visibility.Collapsed;
                        }
                    }
                }
            }
        }

        if (groupRoot.DataContext is not LauncherItem group || group.IsExpanded) return;

        // Restore collapsed state after re-render
        foreach (var child in groupRoot.Children)
        {
            if (child is StackPanel childPanel && childPanel.Tag as string == "GroupChildren")
            {
                childPanel.Visibility = Visibility.Collapsed;
                break;
            }
        }

        // Update chevron icon to right-pointing
        if (groupRoot.Children[0] is Grid header)
        {
            foreach (var headerChild in header.Children)
            {
                if (headerChild is Button btn && btn.Content is FontIcon icon)
                {
                    icon.Glyph = "\uE76C";
                    break;
                }
            }
        }
    }

    private void ItemCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border || border.Child is not Panel panel) return;

        ApplyIconModeGroupCardLayout(border);

        // For regular items, child is a Grid.
        // For groups, child is a StackPanel (GroupRoot) whose first child is the header Grid.
        Grid? grid = panel as Grid ?? (panel as StackPanel)?.Children.FirstOrDefault() as Grid;
        if (grid == null) return;

        foreach (var child in grid.Children)
        {
            if (child is not FrameworkElement fe) continue;

            // Hide drag grip in readonly mode (Column 0 FontIcon)
            if (_isReadOnly && Grid.GetColumn(fe) == 0 && fe is FontIcon)
                fe.Visibility = Visibility.Collapsed;

            // Hide action buttons on load (use Opacity to preserve layout height)
            // Column 3 for list mode (4-col grid), Column 2 for icon mode (3-col grid)
            int col = Grid.GetColumn(fe);
            if ((col == 3 || col == 2) && fe is StackPanel sp && sp.Orientation == Orientation.Horizontal)
            {
                sp.Opacity = 0;
                sp.IsHitTestVisible = false;
            }
        }
    }

    private void ItemCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is Border border)
        {
            if (FindGripIcon(border) is FontIcon grip)
                grip.Opacity = 0.8;
            // Show buttons for both regular items and groups
            if (FindButtonsPanel(border) is StackPanel buttons)
            {
                buttons.Opacity = 1;
                buttons.IsHitTestVisible = true;
            }
        }
        this.ProtectedCursor = _sizeAllCursor;
    }

    private void ItemCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is Border border)
        {
            if (FindGripIcon(border) is FontIcon grip)
                grip.Opacity = 0.3;
            // Hide buttons for both regular items and groups
            if (FindButtonsPanel(border) is StackPanel buttons)
            {
                buttons.Opacity = 0;
                buttons.IsHitTestVisible = false;
            }
        }
        this.ProtectedCursor = _arrowCursor;
    }

    private void ItemButtons_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        this.ProtectedCursor = _arrowCursor;
        e.Handled = true;
    }

    private void ItemButtons_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        this.ProtectedCursor = _sizeAllCursor;
        e.Handled = true;
    }

    private void ItemCard_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is not FrameworkElement anchor || anchor.DataContext is not LauncherItem item) return;

        ShowItemContextMenu(anchor, item, item.IsGroup);
        e.Handled = true;
    }

    private static bool IsGroupCard(Border border) =>
        border.Child is StackPanel sp && sp.Tag as string == "GroupRoot";

    private static Border? FindParentBorder(DependencyObject? element)
    {
        var current = element as FrameworkElement;
        while (current != null)
        {
            if (current is Border border) return border;
            current = current.Parent as FrameworkElement;
        }
        return null;
    }

    private static FontIcon? FindGripIcon(Border border)
    {
        // The grip icon is the first FontIcon child of the first Grid/StackPanel inside the Border
        if (border.Child is Panel panel)
        {
            // For group template, child is StackPanel > Grid; for item/heading, child is Grid directly
            var grid = panel is Grid g ? g : (panel.Children.FirstOrDefault() as Grid);
            if (grid?.Children.FirstOrDefault() is FontIcon icon)
                return icon;
        }
        return null;
    }

    private static StackPanel? FindButtonsPanel(Border border)
    {
        // The buttons panel is the last-column StackPanel in the header Grid
        // Column 3 for list mode (4-col grid), Column 2 for icon mode (3-col grid)
        if (border.Child is not Panel panel) return null;
        var grid = panel as Grid ?? (panel as StackPanel)?.Children.FirstOrDefault() as Grid;
        if (grid == null) return null;
        foreach (var child in grid.Children)
            if (child is StackPanel sp && (Grid.GetColumn(sp) == 3 || Grid.GetColumn(sp) == 2))
                return sp;
        return null;
    }

    private void ToggleGroupExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        // Walk up to the StackPanel tagged "GroupRoot"
        DependencyObject? current = button;
        while (current != null)
        {
            if (current is StackPanel sp && sp.Tag as string == "GroupRoot")
            {
                // Update model state
                if (sp.DataContext is LauncherItem group)
                    group.IsExpanded = !group.IsExpanded;

                foreach (var child in sp.Children)
                {
                    if (child is StackPanel childPanel && childPanel.Tag as string == "GroupChildren")
                    {
                        bool wasCollapsed = childPanel.Visibility == Visibility.Collapsed;
                        childPanel.Visibility = wasCollapsed ? Visibility.Visible : Visibility.Collapsed;

                        if (button.Content is FontIcon icon)
                            icon.Glyph = wasCollapsed ? "\uE70D" : "\uE76C";

                        break;
                    }
                }
                break;
            }
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
    }

    private void ItemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is not FrameworkElement fe || fe.Tag is not LauncherItem item) return;
        ShowItemContextMenu(fe, item, isGroup: false);
    }

    private void GroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is not FrameworkElement fe || fe.Tag is not LauncherItem item) return;
        ShowItemContextMenu(fe, item, isGroup: true);
    }

    private void ShowItemContextMenu(FrameworkElement anchor, LauncherItem item, bool isGroup)
    {
        var flyout = new MenuFlyout();

        var moveUp = new MenuFlyoutItem { Text = "Move up", Icon = new FontIcon { Glyph = "\uE70E" } };
        moveUp.Click += (s, ev) =>
        {
            var parent = FindParentCollection(item);
            if (parent == null) return;
            int index = parent.IndexOf(item);
            if (index > 0) { parent.Move(index, index - 1); SaveAndUpdateTaskbar(); }
        };
        flyout.Items.Add(moveUp);

        var moveDown = new MenuFlyoutItem { Text = "Move down", Icon = new FontIcon { Glyph = "\uE70D" } };
        moveDown.Click += (s, ev) =>
        {
            var parent = FindParentCollection(item);
            if (parent == null) return;
            int index = parent.IndexOf(item);
            if (index >= 0 && index < parent.Count - 1) { parent.Move(index, index + 1); SaveAndUpdateTaskbar(); }
        };
        flyout.Items.Add(moveDown);

        {
            // Build "Move to..." sub-menu
            var items = CurrentItems;
            ObservableCollection<LauncherItem>? currentParent = null;
            LauncherItem? currentGroup = null;

            if (!isGroup)
            {
                foreach (var group in items.Where(i => i.IsGroup))
                {
                    if (group.Children.Contains(item))
                    {
                        currentParent = group.Children;
                        currentGroup = group;
                        break;
                    }
                }
            }

            var moveToSub = new MenuFlyoutSubItem { Text = "Move to\u2026", Icon = new FontIcon { Glyph = "\uE8DE" } };

            if (!isGroup)
            {
                if (currentParent != null)
                {
                    var topLevel = new MenuFlyoutItem { Text = "Top Level", Icon = new FontIcon { Glyph = "\uE74B" } };
                    topLevel.Click += (s, ev) =>
                    {
                        currentParent.Remove(item);
                        items.Add(item);
                        SaveAndUpdateTaskbar();
                    };
                    moveToSub.Items.Add(topLevel);
                }

                foreach (var group in items.Where(i => i.IsGroup))
                {
                    if (group == currentGroup) continue;
                    var targetGroup = group;
                    var groupOption = new MenuFlyoutItem { Text = group.Name, Icon = new FontIcon { Glyph = "\uF168" } };
                    groupOption.Click += (s, ev) =>
                    {
                        (currentParent ?? items).Remove(item);
                        targetGroup.Children.Add(item);
                        SaveAndUpdateTaskbar();
                    };
                    moveToSub.Items.Add(groupOption);
                }
            }

            // Add "Move to another launcher" options
            var launchers = SettingsManager.Current.Launchers;
            var otherLaunchers = launchers.Where(l => l != TargetLauncher).ToList();
            if (otherLaunchers.Count > 0)
            {
                if (moveToSub.Items.Count > 0)
                    moveToSub.Items.Add(new MenuFlyoutSeparator());

                foreach (var launcher in otherLaunchers)
                {
                    var targetLauncher = launcher;
                    var launcherOption = new MenuFlyoutItem
                    {
                        Text = $"{targetLauncher.Name} (launcher)",
                        Icon = new FontIcon { Glyph = "\uF0E2" },
                    };
                    launcherOption.Click += (s, ev) =>
                    {
                        // Remove from current location
                        if (!isGroup)
                            (currentParent ?? items).Remove(item);
                        else
                            items.Remove(item);
                        // Add to target launcher
                        targetLauncher.Items.Add(item);
                        SettingsManager.SaveSettings();
                        Services.AutoSyncService.NotifyItemsChanged();
                        FlyoutWindow.InvalidateItems(TargetLauncher?.Id);
                        FlyoutWindow.InvalidateItems(targetLauncher.Id);
                        RebuildColumns();
                    };
                    moveToSub.Items.Add(launcherOption);
                }
            }

            if (moveToSub.Items.Count > 0)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(moveToSub);
            }
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var edit = new MenuFlyoutItem { Text = "Edit", Icon = new FontIcon { Glyph = "\uE70F" } };
        edit.Click += async (s, ev) =>
        {
            if (item.IsGroup) await ShowGroupDialog(item);
            else await ShowItemDialog(item);
        };
        flyout.Items.Add(edit);

        var remove = new MenuFlyoutItem { Text = "Remove", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += (s, ev) =>
        {
            var items = CurrentItems;
            if (!items.Remove(item))
            {
                foreach (var group in items.Where(i => i.IsGroup))
                    if (group.Children.Remove(item)) break;
            }
            SaveAndUpdateTaskbar();
        };
        flyout.Items.Add(remove);

        flyout.ShowAt(anchor);
    }

    private void MoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is not FrameworkElement fe || fe.Tag is not LauncherItem item) return;

        var items = CurrentItems;

        // Find the item's current parent collection
        ObservableCollection<LauncherItem>? currentParent = null;
        LauncherItem? currentGroup = null;
        foreach (var group in items.Where(i => i.IsGroup))
        {
            if (group.Children.Contains(item))
            {
                currentParent = group.Children;
                currentGroup = group;
                break;
            }
        }

        var flyout = new MenuFlyout();

        // If in a group, offer "Move to Top Level"
        if (currentParent != null)
        {
            var topLevelOption = new MenuFlyoutItem
            {
                Text = "Top Level",
                Icon = new FontIcon { Glyph = "\uE74B" }
            };
            topLevelOption.Click += (s, ev) =>
            {
                currentParent.Remove(item);
                items.Add(item);
                SaveAndUpdateTaskbar();
            };
            flyout.Items.Add(topLevelOption);
        }

        // Offer each group as a target (except the item's current group)
        foreach (var group in items.Where(i => i.IsGroup))
        {
            if (group == currentGroup) continue;
            var targetGroup = group;
            var groupOption = new MenuFlyoutItem
            {
                Text = group.Name,
                Icon = new FontIcon { Glyph = "\uF168" }
            };
            groupOption.Click += (s, ev) =>
            {
                (currentParent ?? items).Remove(item);
                targetGroup.Children.Add(item);
                SaveAndUpdateTaskbar();
            };
            flyout.Items.Add(groupOption);
        }

        if (flyout.Items.Count > 0)
            flyout.ShowAt(fe);
    }

    private async void ShowAddDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        await ShowItemDialog(null);
    }

    private async void ShowAddGroupDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        await ShowGroupDialog(null);
    }

    private void AddColumn_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        CurrentItems.Add(LauncherItem.CreateColumnBreak());
        RebuildColumns();
        SaveAndUpdateTaskbar();
    }

    private void RemoveColumn_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is not FrameworkElement fe || fe.Tag is not int colIdx) return;
        if (colIdx <= 0 || colIdx >= _columnLists.Count) return;

        // Move items from the removed column into the previous column
        var removedCol = _columnLists[colIdx];
        var prevCol = _columnLists[colIdx - 1];
        foreach (var item in removedCol)
            prevCol.Add(item);

        _columnLists.RemoveAt(colIdx);
        SyncColumnsToFlatList();
        SaveAndUpdateTaskbar();
    }

    private async void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is FrameworkElement fe && fe.Tag is LauncherItem item)
        {
            if (item.IsColumnBreak) return; // column breaks have no editable properties
            if (item.IsGroup)
                await ShowGroupDialog(item);
            else
                await ShowItemDialog(item);
        }
    }

    private async Task ShowGroupDialog(LauncherItem? existingItem)
    {
        bool isEdit = existingItem != null;

        var nameBox = new TextBox
        {
            PlaceholderText = "Group name",
            Margin = new Thickness(0, 0, 0, 8)
        };

        if (isEdit)
            nameBox.Text = existingItem!.Name;

        var validationHint = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 255, 69, 0)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var form = new StackPanel { MinWidth = 400 };
        form.Children.Add(Label("Name"));
        form.Children.Add(nameBox);
        form.Children.Add(validationHint);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = isEdit ? "Edit Group" : "Add Group",
            Content = form,
            PrimaryButtonText = isEdit ? "Save" : "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        void ValidateForm()
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                validationHint.Text = "Name is required.";
                validationHint.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = false;
            }
            else
            {
                validationHint.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
            }
        }

        nameBox.TextChanged += (s, ev) => ValidateForm();
        ValidateForm();

        var result = await dialog.ShowAsync();

        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();

        if (isEdit)
        {
            existingItem!.Name = name;
        }
        else
        {
            CurrentItems.Add(LauncherItem.CreateGroup(name));
        }

        SaveAndUpdateTaskbar();
    }

    private async void ShowAddItemToGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is FrameworkElement fe && fe.Tag is LauncherItem group)
            await ShowItemDialog(null, group.Children);
    }

    private void GroupChildList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is ListView lv && lv.Tag is LauncherItem group
            && e.Items.FirstOrDefault() is LauncherItem item)
        {
            _dragItem = item;
            _dragSourceCollection = group.Children;
            e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

            // Show the top-level drop zone so the user can pull items out of groups.
            TopLevelDropZone.Visibility = Visibility.Visible;
            TopLevelDropZone.BorderThickness = new Thickness(2);
            ShowNewColumnDropZones();
        }
    }

    private void GroupChildList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView lv || lv.Tag is not LauncherItem group) return;
        if (_dragItem == null || _dragSourceCollection == null) return;

        // Reject groups and column breaks from being dropped into a group.
        if (_dragItem.IsGroup || _dragItem.IsColumnBreak)
        {
            e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        // Show insertion indicator within the group
        int dropIndex = GetDropIndex(lv, e);
        ShowInsertionIndicator(lv, dropIndex);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        if (dropIndex < group.Children.Count)
        {
            bool isGrid = IsGridItemsPanel(lv.ItemsPanelRoot);
            e.DragUIOverride.Caption = isGrid
                ? $"Move before {group.Children[dropIndex].Name}"
                : $"Move above {group.Children[dropIndex].Name}";
        }
        else if (group.Children.Count == 0)
            e.DragUIOverride.Caption = $"Move into {group.Name}";
        else
            e.DragUIOverride.Caption = $"Move to end of {group.Name}";

        e.Handled = true;
    }

    private void GroupChildList_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
    }

    private void GroupChildList_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        HideNewColumnDropZones();
        if (sender is not ListView lv || lv.Tag is not LauncherItem group) return;
        if (_dragItem == null || _dragSourceCollection == null || _dragItem.IsGroup || _dragItem.IsColumnBreak) return;

        int dropIndex = GetDropIndex(lv, e);

        // If reordering within the same group, adjust for the removal shift
        int originalIndex = _dragSourceCollection == group.Children ? _dragSourceCollection.IndexOf(_dragItem) : -1;
        _dragSourceCollection.Remove(_dragItem);
        if (originalIndex >= 0 && originalIndex < dropIndex)
            dropIndex--;
        if (dropIndex > group.Children.Count) dropIndex = group.Children.Count;
        group.Children.Insert(dropIndex, _dragItem);

        _dragItem = null;
        _dragSourceCollection = null;
        TopLevelDropZone.Visibility = Visibility.Collapsed;

        SyncColumnsToFlatList();
        bool columnsChanged = AutoRemoveEmptyColumns();
        if (columnsChanged)
        {
            SyncColumnsToFlatList();
            RebuildColumns();
        }
        SaveAndUpdateTaskbar();
        e.Handled = true;
    }

    private void GroupChildList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        HideNewColumnDropZones();
        TopLevelDropZone.Visibility = Visibility.Collapsed;
        _dragItem = null;
        _dragSourceCollection = null;
    }

    // -- Top-level drop zone (visible when dragging from a group) --

    private void TopLevelDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (_dragItem == null || _dragSourceCollection == null) return;
        // Only accept drops from group children (not from column lists)
        if (_columnLists.Contains(_dragSourceCollection)) return;

        ClearInsertionIndicator();
        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = "Move to top level";
        e.Handled = true;
    }

    private void TopLevelDropZone_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        HideNewColumnDropZones();
        if (_dragItem == null || _dragSourceCollection == null) return;
        // Only accept drops from group children
        if (_columnLists.Contains(_dragSourceCollection)) return;

        _dragSourceCollection.Remove(_dragItem);
        // Add to the last column
        if (_columnLists.Count > 0)
            _columnLists[^1].Add(_dragItem);

        _dragItem = null;
        _dragSourceCollection = null;
        TopLevelDropZone.Visibility = Visibility.Collapsed;

        SyncColumnsToFlatList();
        SaveAndUpdateTaskbar();
        e.Handled = true;
    }

    // -- New-column drop zone --

    private void NewColumnZone_DragOver(object sender, DragEventArgs e)
    {
        if (_dragItem == null || _dragSourceCollection == null) return;

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = "New column";
        if (sender is Border zone)
            zone.BorderThickness = new Thickness(2);
        e.Handled = true;
    }

    private void NewColumnZone_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border zone)
            zone.BorderThickness = new Thickness(0);
    }

    private void NewColumnZone_Drop(object sender, DragEventArgs e)
    {
        if (_dragItem == null || _dragSourceCollection == null) return;

        int insertAt = sender is Border zone && zone.Tag is int pos
            ? pos : _columnLists.Count;

        _dragSourceCollection.Remove(_dragItem);

        // Create new column with the dropped item at the specified position
        var newCol = new ObservableCollection<LauncherItem> { _dragItem };
        _columnLists.Insert(insertAt, newCol);

        _dragItem = null;
        _dragSourceCollection = null;

        SyncColumnsToFlatList();
        AutoRemoveEmptyColumns();
        SaveAndUpdateTaskbar();
        RebuildColumns();
        e.Handled = true;
    }

    /// <summary>
    /// Removes any empty columns from _columnLists and re-syncs the flat list.
    /// Called after drops that might leave a source column empty.
    /// </summary>
    private bool AutoRemoveEmptyColumns()
    {
        // Always keep at least one column
        bool removed = false;
        for (int i = _columnLists.Count - 1; i >= 0; i--)
        {
            if (_columnLists[i].Count == 0 && _columnLists.Count > 1)
            {
                _columnLists.RemoveAt(i);
                removed = true;
            }
        }
        return removed;
    }

    private void ShowNewColumnDropZones()
    {
        foreach (var zone in _newColumnDropZones)
        {
            zone.Visibility = Visibility.Visible;
            zone.BorderThickness = new Thickness(0);
        }
        // Expand MaxWidth to accommodate visible drop zones so the view doesn't scroll
        int dropZoneWidth = _newColumnDropZones.Count * 48;
        RootGrid.MaxWidth = CalculateTotalColumnsWidth() + dropZoneWidth;
    }

    private void HideNewColumnDropZones()
    {
        foreach (var zone in _newColumnDropZones)
        {
            zone.Visibility = Visibility.Collapsed;
            zone.BorderThickness = new Thickness(0);
        }
        // Restore normal MaxWidth
        RootGrid.MaxWidth = CalculateTotalColumnsWidth();
    }

    private int CalculateTotalColumnsWidth()
    {
        int colFixedWidth = LauncherViewModes.IsIconMode(TargetLauncher?.ViewMode ?? LauncherViewModes.Icon)
            ? GetIconModeColumnWidth()
            : ListModeColumnWidth;
        int cols = _columnLists.Count;
        int gaps = cols > 1 ? (cols - 1) * ColumnSpacing : 0;
        int natural = cols * colFixedWidth + gaps + ColumnLayoutPadding;
        // Minimum width = 2 columns so single-column view isn't too narrow
        int minWidth = 2 * colFixedWidth + ColumnSpacing + ColumnLayoutPadding;
        return Math.Max(minWidth, natural);
    }

    // -- Insertion indicator helpers --

    private void ShowInsertionIndicator(ListViewBase listView, int dropIndex)
    {
        ClearInsertionIndicator();

        // Empty list: highlight the ListView itself as a drop target
        if (listView.Items.Count == 0)
        {
            _lastIndicatorListView = listView;
            listView.BorderBrush = AccentBrush;
            listView.BorderThickness = new Thickness(2);
            return;
        }

        bool isGrid = IsGridItemsPanel(listView.ItemsPanelRoot);

        int targetIndex = dropIndex < listView.Items.Count ? dropIndex : listView.Items.Count - 1;
        if (targetIndex < 0) return;
        if (listView.ContainerFromIndex(targetIndex) is not Control container) return;

        _lastIndicatorContainer = container;
        container.BorderBrush = AccentBrush;

        if (isGrid)
        {
            // Icon grid: vertical line to the left/right of a tile.
            // Use negative padding to steal space from inside the container so
            // the outer dimensions stay constant and the wrap grid doesn't reflow.
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
            // List mode: horizontal line above/below
            container.BorderThickness = dropIndex < listView.Items.Count
                ? new Thickness(0, 3, 0, 0)
                : new Thickness(0, 0, 0, 3);
        }
    }

    private void ClearInsertionIndicator()
    {
        if (_lastIndicatorContainer != null)
        {
            _lastIndicatorContainer.BorderBrush = null;
            _lastIndicatorContainer.BorderThickness = new Thickness(0);
            _lastIndicatorContainer.Padding = new Thickness(0);
            _lastIndicatorContainer.Margin = new Thickness(0);
            _lastIndicatorContainer = null;
        }
        if (_lastIndicatorListView != null)
        {
            _lastIndicatorListView.BorderBrush = null;
            _lastIndicatorListView.BorderThickness = new Thickness(0);
            _lastIndicatorListView = null;
        }
    }

    private async Task ShowItemDialog(LauncherItem? existingItem, ObservableCollection<LauncherItem>? targetList = null)
    {
        bool isEdit = existingItem != null;

        // Track state for this dialog session
        string fetchedIconPath = existingItem?.IconPath ?? "";
        string? customGlyph = isEdit ? existingItem!.IconGlyph : null;
        string customColor = existingItem?.IconColor ?? "";
        bool isWebsite = existingItem?.IsWebsite ?? true;
        bool isPwa = existingItem?.IsPwa ?? false;
        bool openInAppWindow = existingItem?.OpenInAppWindow ?? false;
        string appWindowBrowser = existingItem?.AppWindowBrowser ?? "";
        string appWindowBrowserProfile = existingItem?.AppWindowBrowserProfile ?? "";

        // -- 1. Shared dialog state --
        Microsoft.UI.Dispatching.DispatcherQueueTimer? debounceTimer = null;
        string lastFetchedPath = "";
        bool populating = false;

        // Derived target state, kept in sync by SyncDerived(). The active tab decides the
        // source: "list" = the app/PWA selection, "custom" = the typed path/link.
        // (isWebsite / isPwa declared above.)
        string effectiveTarget = existingItem?.Path ?? "";
        string currentTab = "list";

        // Declared early so the validation handlers can reference it before the form is built.
        var validationHint = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 255, 69, 0)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        // The dialog is created up front so validation handlers can toggle its primary
        // button safely; its Content is assigned once the form is built below.
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = isEdit ? "Edit Item" : "Add Item",
            PrimaryButtonText = isEdit ? "Save" : "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        // -- 2. Unified app + PWA picker (search + icon list) --
        var searchBox = new AutoSuggestBox
        {
            QueryIcon = new SymbolIcon(Symbol.Find),
            PlaceholderText = "Loading apps\u2026",
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var appList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Height = 260,
            Margin = new Thickness(0, 0, 0, 8),
            ItemTemplate = BuildAppItemTemplate()
        };
        ScrollViewer.SetVerticalScrollBarVisibility(appList, ScrollBarVisibility.Auto);

        // Custom path/link inputs (live in the "Open a file or link instead" pane).
        var pathBox = new TextBox
        {
            PlaceholderText = @"C:\path\to\app.exe  or  https://example.com",
            Margin = new Thickness(0, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var browseButton = new Button
        {
            Content = "Browse",
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 0, 0)
        };
        var allEntries = new List<AppPickerEntry>();
        AppPickerEntry? pickedEntry = null;
        bool catalogLoaded = false;
        bool pendingEditSelect = false;

        // Enumerating apps (Start Menu + registry + shell:AppsFolder) and PWAs is
        // expensive and apartment-threaded, so build the catalog on a background STA thread.
        static List<AppPickerEntry> BuildCatalog()
        {
            var list = new List<AppPickerEntry>();
            foreach (var app in GetInstalledApplications())
                list.Add(new AppPickerEntry(app.DisplayName, app.ExePath, false, app.ExePath));
            foreach (var pwa in GetInstalledPwas())
                list.Add(new AppPickerEntry(pwa.DisplayName, pwa.Aumid, true, $@"shell:AppsFolder\{pwa.Aumid}"));
            return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        void FilterApps(string? query)
        {
            IEnumerable<AppPickerEntry> items = allEntries;
            if (!string.IsNullOrWhiteSpace(query))
                items = items.Where(a => a.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
            var shown = items.ToList();
            populating = true;
            appList.ItemsSource = shown;
            if (pickedEntry != null && shown.Contains(pickedEntry))
                appList.SelectedItem = pickedEntry;
            populating = false;
        }

        async Task EnsureCatalogLoadedAsync()
        {
            if (catalogLoaded) return;
            catalogLoaded = true;
            allEntries = await AppPickerService.RunStaAsync(BuildCatalog) ?? new List<AppPickerEntry>();
            searchBox.PlaceholderText = "Search apps and web apps\u2026";
            FilterApps(searchBox.Text);
            AppPickerService.LoadIcons(allEntries, DispatcherQueue);
            if (pendingEditSelect)
            {
                pendingEditSelect = false;
                PreselectEditEntry();
            }
        }

        // -- Target resolution: a list selection wins; otherwise the typed path/link. --
        static bool LooksLikeFilePath(string p) =>
            p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
            (p.Length >= 2 && p[1] == ':') ||
            p.StartsWith(@"\\") ||
            p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

        static bool LooksLikeWebUrl(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return false;
            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
            if (LooksLikeFilePath(p)) return false;
            return p.Contains('.');
        }

        (string target, bool isPwa, bool isWebsite) ResolveTarget()
        {
            if (currentTab == "custom")
            {
                var t = pathBox.Text.Trim();
                return (t, false, LooksLikeWebUrl(t));
            }
            if (appList.SelectedItem is AppPickerEntry e)
                return (e.LaunchPath, e.IsPwa, false);
            return ("", false, false);
        }

        void PreselectEditEntry()
        {
            var match = allEntries.FirstOrDefault(e =>
                e.IsPwa == existingItem!.IsPwa &&
                string.Equals(e.LaunchPath, existingItem.Path, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                pickedEntry = match;
                populating = true;
                if (appList.ItemsSource is IEnumerable<AppPickerEntry> shown && shown.Contains(match))
                    appList.SelectedItem = match;
                populating = false;
                SyncDerived();
                ValidateForm();
            }
            else if (!existingItem!.IsWebsite)
            {
                // Not in the catalog (e.g. a hand-browsed exe) \u2014 show it on the File/link tab.
                populating = true;
                pathBox.Text = existingItem.Path;
                populating = false;
                ShowTabPanel("custom");
            }
        }

        static DataTemplate BuildAppItemTemplate() => (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            "<Grid ColumnSpacing=\"12\" Padding=\"0,4\">" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>" +
            "<Image Grid.Column=\"0\" Source=\"{Binding Icon}\" Width=\"24\" Height=\"24\"/>" +
            "<TextBlock Grid.Column=\"1\" Text=\"{Binding Name}\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\"/>" +
            "</Grid></DataTemplate>");

        // -- 3. Arguments (Application only) --
        var argsLabel = Label("Arguments");
        var argsBox = new TextBox
        {
            PlaceholderText = "(optional)",
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // -- 4. Web app window mode (Website only) --
        var appWindowToggle = new ToggleSwitch
        {
            Header = "Open as app window",
            OffContent = "Use normal browser tab",
            OnContent = "Open in standalone window",
            IsOn = openInAppWindow,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // -- 4a. Browser picker --
        var browserLabel = Label("Browser");
        var browserCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        var installedBrowsers = GetInstalledBrowsers();
        browserCombo.Items.Add(new ComboBoxItem { Content = "Default browser", Tag = "" });
        foreach (var browser in installedBrowsers)
            browserCombo.Items.Add(new ComboBoxItem { Content = browser.DisplayName, Tag = browser.ExePath });
        browserCombo.Items.Add(new ComboBoxItem { Content = "Custom\u2026", Tag = "__custom__" });

        // -- 4b. Profile picker --
        var profileLabel = Label("Profile");
        var profileCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };

        void PopulateProfileCombo()
        {
            profileCombo.Items.Clear();

            BrowserEngine currentEngine;
            if (string.IsNullOrEmpty(appWindowBrowser))
            {
                string? defaultExe = GetDefaultBrowserExePath();
                currentEngine = defaultExe != null ? DetectEngine(defaultExe) : BrowserEngine.Chromium;
            }
            else
            {
                var match = installedBrowsers.FirstOrDefault(b =>
                    string.Equals(b.ExePath, appWindowBrowser, StringComparison.OrdinalIgnoreCase));
                currentEngine = match?.Engine ?? DetectEngine(appWindowBrowser);
            }

            profileCombo.Items.Add(new ComboBoxItem { Content = "App sandbox (isolated)", Tag = "" });

            if (currentEngine != BrowserEngine.Gecko)
            {
                if (string.IsNullOrEmpty(appWindowBrowser))
                {
                    profileCombo.Items.Add(new ComboBoxItem { Content = "Default profile", Tag = "__default__" });
                }
                else
                {
                    var match = installedBrowsers.FirstOrDefault(b =>
                        string.Equals(b.ExePath, appWindowBrowser, StringComparison.OrdinalIgnoreCase));
                    string profileDataDir = match?.ProfileDataDir ?? "";

                    foreach (var profile in GetBrowserProfiles(profileDataDir, currentEngine))
                    {
                        string label = profile.DisplayName == profile.DirectoryName
                            ? profile.DisplayName
                            : $"{profile.DisplayName} ({Path.GetFileName(profile.DirectoryName)})";
                        profileCombo.Items.Add(new ComboBoxItem { Content = label, Tag = profile.DirectoryName });
                    }
                }
            }

            int selectedIndex = 0;
            if (!string.IsNullOrEmpty(appWindowBrowserProfile))
            {
                for (int i = 1; i < profileCombo.Items.Count; i++)
                {
                    if (profileCombo.Items[i] is ComboBoxItem ci &&
                        string.Equals(ci.Tag as string, appWindowBrowserProfile, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }
            profileCombo.SelectedIndex = selectedIndex;
        }

        profileCombo.SelectionChanged += (s, ev) =>
        {
            if (profileCombo.SelectedItem is ComboBoxItem selected)
                appWindowBrowserProfile = selected.Tag as string ?? "";
        };

        // Select existing browser
        int selectedBrowserIndex = 0;
        if (!string.IsNullOrEmpty(appWindowBrowser))
        {
            for (int i = 1; i < browserCombo.Items.Count - 1; i++)
            {
                if (browserCombo.Items[i] is ComboBoxItem ci &&
                    string.Equals(ci.Tag as string, appWindowBrowser, StringComparison.OrdinalIgnoreCase))
                {
                    selectedBrowserIndex = i;
                    break;
                }
            }
            if (selectedBrowserIndex == 0 && appWindowBrowser != "")
            {
                var customItem = new ComboBoxItem
                {
                    Content = Path.GetFileNameWithoutExtension(appWindowBrowser),
                    Tag = appWindowBrowser
                };
                browserCombo.Items.Insert(browserCombo.Items.Count - 1, customItem);
                selectedBrowserIndex = browserCombo.Items.Count - 2;
            }
        }
        browserCombo.SelectedIndex = selectedBrowserIndex;

        browserCombo.SelectionChanged += async (s, ev) =>
        {
            if (browserCombo.SelectedItem is ComboBoxItem selected)
            {
                string tag = selected.Tag as string ?? "";
                if (tag == "__custom__")
                {
                    var picker = new FileOpenPicker();
                    picker.FileTypeFilter.Add(".exe");
                    InitializePicker(picker);
                    var file = await picker.PickSingleFileAsync();
                    if (file != null)
                    {
                        appWindowBrowser = file.Path;
                        var customItem = new ComboBoxItem
                        {
                            Content = Path.GetFileNameWithoutExtension(file.Path),
                            Tag = file.Path
                        };
                        browserCombo.Items.Insert(browserCombo.Items.Count - 1, customItem);
                        browserCombo.SelectedItem = customItem;
                    }
                    else
                    {
                        browserCombo.SelectedIndex = 0;
                        appWindowBrowser = "";
                    }
                }
                else
                {
                    appWindowBrowser = tag;
                }
                PopulateProfileCombo();
            }
        };

        PopulateProfileCombo();

        // -- App window sub-options panel --
        var appWindowOptionsPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        appWindowOptionsPanel.Children.Add(browserLabel);
        appWindowOptionsPanel.Children.Add(browserCombo);
        appWindowOptionsPanel.Children.Add(profileLabel);
        appWindowOptionsPanel.Children.Add(profileCombo);

        void UpdateAppWindowOptionsVisibility()
        {
            appWindowOptionsPanel.Visibility = openInAppWindow && isWebsite
                ? Visibility.Visible : Visibility.Collapsed;
        }

        appWindowToggle.Toggled += (s, ev) =>
        {
            openInAppWindow = appWindowToggle.IsOn;
            UpdateAppWindowOptionsVisibility();
        };

        // -- 5. Name --
        var nameBox = new TextBox
        {
            PlaceholderText = "(optional)",
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // -- 6. Icon --
        var iconPreview = new Image
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconGlyphPreview = new FontIcon
        {
            FontSize = 24,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var iconEmojiPreview = new TextBlock
        {
            FontSize = 24,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var iconStatus = new TextBlock
        {
            Text = "Auto-detected",
            FontSize = 12,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        iconRow.Children.Add(iconPreview);
        iconRow.Children.Add(iconGlyphPreview);
        iconRow.Children.Add(iconEmojiPreview);
        iconRow.Children.Add(iconStatus);

        var refreshButton = new Button
        {
            Content = "Retry",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12
        };
        iconRow.Children.Add(refreshButton);

        // -- Icon gallery flyout --
        void RefreshIconPreview()
        {
            // Hide all previews first
            iconPreview.Source = null;
            iconPreview.Visibility = Visibility.Collapsed;
            iconGlyphPreview.Visibility = Visibility.Collapsed;
            iconEmojiPreview.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(fetchedIconPath) && File.Exists(fetchedIconPath))
            {
                try
                {
                    iconPreview.Source = new BitmapImage { DecodePixelWidth = 32, UriSource = new Uri(fetchedIconPath, UriKind.Absolute) };
                    iconPreview.Visibility = Visibility.Visible;
                    iconStatus.Text = customGlyph != null ? "Custom image" : (isWebsite ? "Auto favicon" : "Auto icon");
                }
                catch
                {
                    iconStatus.Text = "Failed to load icon";
                }
            }
            else if (customGlyph != null)
            {
                SolidColorBrush? colorBrush = null;
                if (!string.IsNullOrEmpty(customColor))
                {
                    try
                    {
                        string h = customColor.TrimStart('#');
                        if (h.Length == 6)
                        {
                            byte r = Convert.ToByte(h[..2], 16);
                            byte g = Convert.ToByte(h[2..4], 16);
                            byte b = Convert.ToByte(h[4..6], 16);
                            colorBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
                        }
                    }
                    catch { /* ignore */ }
                }

                if (IconGallery.IsFluentGlyph(customGlyph))
                {
                    iconGlyphPreview.Glyph = customGlyph;
                    iconGlyphPreview.Foreground = colorBrush ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                    iconGlyphPreview.Visibility = Visibility.Visible;
                }
                else
                {
                    iconEmojiPreview.Text = customGlyph;
                    if (colorBrush != null)
                        iconEmojiPreview.Foreground = colorBrush;
                    else
                        iconEmojiPreview.ClearValue(TextBlock.ForegroundProperty);
                    iconEmojiPreview.Visibility = Visibility.Visible;
                }
                iconStatus.Text = "Custom icon";
            }
            else
            {
                iconStatus.Text = "No icon";
            }
        }

        var chooseIconButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE790", FontSize = 12 },
                    new TextBlock { Text = "Choose" }
                }
            },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12
        };
        chooseIconButton.Flyout = IconGallery.CreateFlyout(
            onSelected: result =>
            {
                if (result.Glyph != null)
                {
                    customGlyph = result.Glyph;
                    customColor = result.Color ?? "";
                    fetchedIconPath = "";
                }
                else if (result.ImagePath != null)
                {
                    fetchedIconPath = result.ImagePath;
                    // Keep customGlyph as fallback but image takes priority
                }
                RefreshIconPreview();
            },
            onBrowseRequested: async () =>
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".ico");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                InitializePicker(picker);
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    // Copy to AppData cache for persistence
                    string cacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "LittleLauncher", "icons");
                    Directory.CreateDirectory(cacheDir);
                    string dest = Path.Combine(cacheDir, $"custom-{Guid.NewGuid():N}{Path.GetExtension(file.Path)}");
                    File.Copy(file.Path, dest, true);
                    fetchedIconPath = dest;
                    RefreshIconPreview();
                }
            },
            onReset: () =>
            {
                customGlyph = null;
                customColor = "";
                fetchedIconPath = "";
                iconPreview.Source = null;
                iconPreview.Visibility = Visibility.Collapsed;
                iconGlyphPreview.Visibility = Visibility.Collapsed;
                iconEmojiPreview.Visibility = Visibility.Collapsed;
                iconStatus.Text = "Auto-detected";
                _ = DoFetch(force: true);
            },
            currentGlyph: customGlyph,
            currentColor: customColor,
            currentImagePath: fetchedIconPath
        );
        iconRow.Children.Add(chooseIconButton);

        // Declared here (after nameBox / appWindowToggle exist) so they can read them.
        void SyncDerived()
        {
            var (t, pwa, web) = ResolveTarget();
            effectiveTarget = t;
            isPwa = pwa;
            isWebsite = web;
            appWindowToggle.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
            UpdateAppWindowOptionsVisibility();
        }

        async Task SelectEntry(AppPickerEntry e)
        {
            pickedEntry = e;
            if (string.IsNullOrWhiteSpace(nameBox.Text)) nameBox.Text = e.Name;
            SyncDerived();
            lastFetchedPath = "";
            await DoFetch(force: false);
        }

        // -- Picker event wiring --
        searchBox.TextChanged += (s, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            FilterApps(searchBox.Text);
        };
        appList.SelectionChanged += async (s, ev) =>
        {
            if (populating) return;
            if (appList.SelectedItem is AppPickerEntry e)
                await SelectEntry(e);
            SyncDerived();
            ValidateForm();
        };

        // -- Auto-populate icon / name for the current target (debounced) --
        async Task DoFetch(bool force)
        {
            SyncDerived();
            var path = effectiveTarget;
            if (string.IsNullOrEmpty(path)) return;
            if (!force && path == lastFetchedPath) return;
            lastFetchedPath = path;

            if (isWebsite)
            {
                var fetchPath = path;
                if (!fetchPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !fetchPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (!force && fetchPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        return;

                    fetchPath = "https://" + fetchPath;
                    populating = true;
                    pathBox.Text = fetchPath;
                    populating = false;
                    lastFetchedPath = fetchPath;
                }

                iconStatus.Text = "Fetching...";
                refreshButton.IsEnabled = false;
                nameBox.IsEnabled = false;
                nameBox.PlaceholderText = "Fetching name...";
                var titleTask = FaviconService.FetchWebsiteTitleAsync(fetchPath);
                var iconTask = FaviconService.FetchAndCacheAsync(fetchPath);
                await Task.WhenAll(titleTask, iconTask);
                refreshButton.IsEnabled = true;
                nameBox.IsEnabled = true;
                nameBox.PlaceholderText = "(optional)";

                if (force || string.IsNullOrEmpty(nameBox.Text))
                {
                    var title = titleTask.Result;
                    if (!string.IsNullOrEmpty(title))
                        nameBox.Text = title;
                }

                var iconPath = iconTask.Result;
                if (!string.IsNullOrEmpty(iconPath))
                {
                    fetchedIconPath = iconPath;
                    RefreshIconPreview();
                }
                else
                {
                    iconStatus.Text = "Could not fetch icon";
                }
            }
            else if (isPwa)
            {
                // Prefer the PWA's own web icon/manifest asset; fall back to the shell image.
                iconStatus.Text = "Fetching icon...";
                refreshButton.IsEnabled = false;
                var iconPath = await FaviconService.GetBestPwaIconAsync(path);
                refreshButton.IsEnabled = true;
                if (!string.IsNullOrEmpty(iconPath))
                {
                    fetchedIconPath = iconPath;
                    RefreshIconPreview();
                }
                else
                {
                    iconStatus.Text = "No icon available";
                }
            }
            else if (path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
            {
                // Store / packaged app — extract icon via shell
                string aumid = path[@"shell:AppsFolder\".Length..];
                iconStatus.Text = "Extracting icon...";
                refreshButton.IsEnabled = false;
                nameBox.IsEnabled = false;
                nameBox.PlaceholderText = "Detecting name...";
                var appIcon = FaviconService.GetPwaIconFromShell(aumid);
                refreshButton.IsEnabled = true;
                nameBox.IsEnabled = true;
                nameBox.PlaceholderText = "(optional)";
                if (!string.IsNullOrEmpty(appIcon))
                {
                    fetchedIconPath = appIcon;
                    RefreshIconPreview();
                }
                else
                {
                    iconStatus.Text = "Could not extract icon";
                }
            }
            else
            {
                if (force || string.IsNullOrEmpty(nameBox.Text))
                {
                    var appName = FaviconService.GetApplicationName(path);
                    if (!string.IsNullOrEmpty(appName))
                        nameBox.Text = appName;
                }

                iconStatus.Text = "Extracting icon...";
                refreshButton.IsEnabled = false;
                nameBox.IsEnabled = false;
                nameBox.PlaceholderText = "Detecting name...";
                var appIcon = FaviconService.GetApplicationIcon(path);
                refreshButton.IsEnabled = true;
                nameBox.IsEnabled = true;
                nameBox.PlaceholderText = "(optional)";
                if (!string.IsNullOrEmpty(appIcon))
                {
                    fetchedIconPath = appIcon;
                    RefreshIconPreview();
                }
                else
                {
                    iconStatus.Text = "Could not extract icon";
                }
            }
        }

        void ScheduleFetch()
        {
            if (populating) return;
            debounceTimer?.Stop();
            debounceTimer = DispatcherQueue.CreateTimer();
            debounceTimer.Interval = TimeSpan.FromMilliseconds(800);
            debounceTimer.IsRepeating = false;
            debounceTimer.Tick += async (s, ev) =>
            {
                await DoFetch(force: false);
            };
            debounceTimer.Start();
        }

        pathBox.TextChanged += (s, ev) =>
        {
            if (populating) return;
            SyncDerived();
            ScheduleFetch();
            ValidateForm();
        };
        refreshButton.Click += async (s, ev) =>
        {
            debounceTimer?.Stop();
            lastFetchedPath = "";
            await DoFetch(force: true);
        };

        // -- Browse for an arbitrary file (custom path) --
        async Task BrowseForApp()
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                populating = true;
                pathBox.Text = file.Path;
                populating = false;
                SyncDerived();
                ScheduleFetch();
                ValidateForm();
            }
        }

        browseButton.Click += async (s, ev) => await BrowseForApp();

        // -- Populate for edit mode --
        if (isEdit)
        {
            populating = true;
            argsBox.Text = existingItem!.Arguments;
            nameBox.Text = existingItem.Name;
            if (existingItem.IsWebsite)
            {
                pathBox.Text = existingItem.Path;
                currentTab = "custom";
            }
            else
            {
                // App or PWA: select the matching row once the catalog finishes loading.
                pendingEditSelect = true;
            }
            RefreshIconPreview();
            populating = false;
            SyncDerived();
        }

        // -- Tab 1: app/PWA picker --
        var listPanel = new StackPanel();
        listPanel.Children.Add(searchBox);
        listPanel.Children.Add(appList);

        // -- Tab 2: file / link --
        var pathRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 4,
            Margin = new Thickness(0, 0, 0, 8)
        };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browseButton, 1);
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browseButton);

        var customPanel = new StackPanel { Visibility = Visibility.Collapsed };
        customPanel.Children.Add(Label("Path or link"));
        customPanel.Children.Add(pathRow);
        customPanel.Children.Add(argsLabel);
        customPanel.Children.Add(argsBox);
        customPanel.Children.Add(appWindowToggle);
        customPanel.Children.Add(appWindowOptionsPanel);

        var tabContent = new Grid();
        tabContent.Children.Add(listPanel);
        tabContent.Children.Add(customPanel);

        // -- Tab strip --
        var listTab = new SelectorBarItem { Text = "Apps & web apps", Tag = "list" };
        var customTab = new SelectorBarItem { Text = "File or link", Tag = "custom" };
        var tabBar = new SelectorBar { Margin = new Thickness(0, 0, 0, 4) };
        tabBar.Items.Add(listTab);
        tabBar.Items.Add(customTab);

        void ShowTabPanel(string tag)
        {
            currentTab = tag;
            listPanel.Visibility = tag == "list" ? Visibility.Visible : Visibility.Collapsed;
            customPanel.Visibility = tag == "custom" ? Visibility.Visible : Visibility.Collapsed;
            var targetItem = tag == "custom" ? customTab : listTab;
            if (!ReferenceEquals(tabBar.SelectedItem, targetItem))
            {
                populating = true;
                tabBar.SelectedItem = targetItem;
                populating = false;
            }
            SyncDerived();
            ValidateForm();
            ScheduleFetch();
        }

        tabBar.SelectionChanged += (s, ev) =>
        {
            if (populating) return;
            ShowTabPanel((tabBar.SelectedItem as SelectorBarItem)?.Tag as string ?? "list");
        };

        // -- Build form --
        var form = new StackPanel { MinWidth = 460 };
        form.Children.Add(tabBar);
        form.Children.Add(tabContent);
        form.Children.Add(Label("Name"));
        form.Children.Add(nameBox);
        form.Children.Add(Label("Icon"));
        form.Children.Add(iconRow);
        form.Children.Add(validationHint);

        ShowTabPanel(currentTab);

        dialog.Content = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 620
        };

        void ValidateForm()
        {
            var (t, _, _) = ResolveTarget();
            // While editing, the existing app/PWA row auto-selects once the catalog
            // finishes loading — treat that pending state as valid so Save isn't disabled.
            if (string.IsNullOrWhiteSpace(t) && !(isEdit && pendingEditSelect))
            {
                validationHint.Text = "Choose an app or web app above, or enter a path or link.";
                validationHint.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = false;
            }
            else
            {
                validationHint.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
            }
        }

        ValidateForm();
        _ = EnsureCatalogLoadedAsync();

        var result = await dialog.ShowAsync();
        debounceTimer?.Stop();
        if (result != ContentDialogResult.Primary) return;

        SyncDerived();
        var (finalPath, finalIsPwa, finalIsWebsite) = ResolveTarget();
        finalPath = finalPath.Trim();
        if (string.IsNullOrEmpty(finalPath)) return;

        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            if (pickedEntry != null)
                name = pickedEntry.Name;
            else if (finalIsWebsite)
                name = await FaviconService.FetchWebsiteTitleAsync(finalPath) ?? finalPath;
            else if (finalPath.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(finalPath);
            else
                name = FaviconService.GetApplicationName(finalPath) ?? Path.GetFileNameWithoutExtension(finalPath);
        }
        var args = finalIsPwa ? "" : argsBox.Text.Trim();
        var glyph = customGlyph ?? (finalIsWebsite || finalIsPwa ? "\uE774" : "\uE8E5");

        if (isEdit)
        {
            existingItem!.Name = name;
            existingItem.Path = finalPath;
            existingItem.Arguments = args;
            existingItem.IconGlyph = glyph;
            existingItem.IconPath = fetchedIconPath;
            existingItem.IconColor = customColor;
            existingItem.IsWebsite = finalIsWebsite;
            existingItem.IsPwa = finalIsPwa;
            existingItem.OpenInAppWindow = finalIsWebsite && openInAppWindow;
            existingItem.AppWindowBrowser = finalIsWebsite && openInAppWindow ? appWindowBrowser : "";
            existingItem.AppWindowBrowserProfile = finalIsWebsite && openInAppWindow ? appWindowBrowserProfile : "";
        }
        else
        {
            var newItem = new LauncherItem(name, finalPath, glyph, finalIsWebsite, args, fetchedIconPath, finalIsWebsite && openInAppWindow);
            newItem.IsPwa = finalIsPwa;
            newItem.IconColor = customColor;
            newItem.AppWindowBrowser = finalIsWebsite && openInAppWindow ? appWindowBrowser : "";
            newItem.AppWindowBrowserProfile = finalIsWebsite && openInAppWindow ? appWindowBrowserProfile : "";
            var target = targetList ?? CurrentItems;
            target.Add(newItem);
        }

        SaveAndUpdateTaskbar();
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
        Margin = new Thickness(0, 0, 0, 4)
    };

    /// <summary>
    /// Determines the item insertion index for a drop based on the cursor position
    /// relative to the ListView item containers.
    /// </summary>
    private static int GetDropIndex(ListViewBase listView, DragEventArgs e)
    {
        // Dispatch to grid hit-testing if the ListView uses ItemsWrapGrid
        if (IsGridItemsPanel(listView.ItemsPanelRoot))
            return GetDropIndexGrid(listView, e);

        var position = e.GetPosition(listView);
        for (int i = 0; i < listView.Items.Count; i++)
        {
            if (listView.ContainerFromIndex(i) is not Control container) continue;
            var transform = container.TransformToVisual(listView);
            var point = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
            if (position.Y < point.Y + container.ActualHeight / 2)
                return i;
        }
        return listView.Items.Count;
    }

    /// <summary>
    /// 2D drop index for icon mode grids (ItemsWrapGrid layout).
    /// Finds the closest item boundary based on both X and Y coordinates.
    /// </summary>
    private static int GetDropIndexGrid(ListViewBase listView, DragEventArgs e)
    {
        var position = e.GetPosition(listView);
        int count = listView.Items.Count;
        if (count == 0) return 0;

        // Find which row the cursor is on, then which column
        int bestIndex = count; // default: append
        double bestDist = double.MaxValue;

        for (int i = 0; i < count; i++)
        {
            if (listView.ContainerFromIndex(i) is not Control container) continue;
            var transform = container.TransformToVisual(listView);
            var pt = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));

            double top = pt.Y;
            double bottom = top + container.ActualHeight;
            double left = pt.X;
            double right = left + container.ActualWidth;
            double midX = left + container.ActualWidth / 2;

            // Only consider items on the row the cursor is in (or closest row)
            if (position.Y >= top && position.Y < bottom)
            {
                // Cursor is on this item's row — pick based on X midpoint
                if (position.X < midX)
                    return i; // insert before this item
                // else keep looking at next items on this row
                bestIndex = i + 1;
                bestDist = 0;
            }
            else if (bestDist > 0)
            {
                // Cursor is between rows or below all rows — track closest
                double vertDist = position.Y < top ? top - position.Y : position.Y - bottom;
                if (vertDist < bestDist)
                {
                    bestDist = vertDist;
                    bestIndex = position.X < midX ? i : i + 1;
                }
            }
        }

        return Math.Min(bestIndex, count);
    }

    private static void InitializePicker(object picker)
    {
        var window = SettingsWindow.GetCurrent();
        if (window == null) return;
        var hwnd = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    // -- Browser/app detection helpers --

    private enum BrowserEngine { Chromium, Gecko }

    private record KnownBrowser(string DisplayName, string ExePath, string ProfileDataDir, BrowserEngine Engine);

    private static BrowserEngine DetectEngine(string exePath)
    {
        string? dir = Path.GetDirectoryName(exePath);
        if (dir != null && (File.Exists(Path.Combine(dir, "chrome.dll")) ||
                            File.Exists(Path.Combine(dir, "msedge.dll"))))
            return BrowserEngine.Chromium;

        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (name is "firefox" or "zen" or "waterfox" or "librewolf" or "floorp" or "mercury" or "firedragon")
            return BrowserEngine.Gecko;

        return BrowserEngine.Chromium;
    }

    private static string? GetDefaultBrowserExePath()
    {
        try
        {
            int size = 512;
            var sb = new System.Text.StringBuilder(size);
            int hr = NativeMethods.AssocQueryString(
                NativeMethods.ASSOCF_NONE, NativeMethods.ASSOCSTR_EXECUTABLE,
                "https", "open", sb, ref size);
            if (hr == 0)
            {
                string exePath = sb.ToString();
                if (File.Exists(exePath))
                    return exePath;
            }
        }
        catch { }

        return null;
    }

    private static List<KnownBrowser> GetInstalledBrowsers()
    {
        var browsers = new List<KnownBrowser>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var candidates = new (string Name, string[] Paths, string ProfileDataDir, BrowserEngine Engine)[]
        {
            ("Microsoft Edge", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
            ], Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), BrowserEngine.Chromium),
            ("Google Chrome", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
            ], Path.Combine(localAppData, "Google", "Chrome", "User Data"), BrowserEngine.Chromium),
            ("Brave", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
            ], Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"), BrowserEngine.Chromium),
            ("Vivaldi", [
                Path.Combine(localAppData, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vivaldi", "Application", "vivaldi.exe")
            ], Path.Combine(localAppData, "Vivaldi", "User Data"), BrowserEngine.Chromium),
            ("Chromium", [
                Path.Combine(localAppData, "Chromium", "Application", "chrome.exe")
            ], Path.Combine(localAppData, "Chromium", "User Data"), BrowserEngine.Chromium),
            ("Firefox", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe")
            ], Path.Combine(appData, "Mozilla", "Firefox"), BrowserEngine.Gecko),
            ("Zen", [
                Path.Combine(localAppData, "Programs", "zen", "zen.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zen Browser", "zen.exe"),
                Path.Combine(localAppData, "zen", "zen.exe"),
            ], Path.Combine(appData, "zen"), BrowserEngine.Gecko),
            ("Waterfox", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Waterfox", "waterfox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Waterfox", "waterfox.exe")
            ], Path.Combine(appData, "Waterfox"), BrowserEngine.Gecko),
            ("LibreWolf", [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreWolf", "librewolf.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreWolf", "librewolf.exe")
            ], Path.Combine(appData, "librewolf"), BrowserEngine.Gecko),
        };

        foreach (var (name, paths, profileDataDir, engine) in candidates)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    browsers.Add(new KnownBrowser(name, path, profileDataDir, engine));
                    break;
                }
            }
        }

        return browsers;
    }

    private record BrowserProfile(string DirectoryName, string DisplayName);

    private static List<BrowserProfile> GetBrowserProfiles(string profileDataDir, BrowserEngine engine)
    {
        if (string.IsNullOrEmpty(profileDataDir) || !Directory.Exists(profileDataDir))
            return [];

        return engine == BrowserEngine.Gecko
            ? GetGeckoProfiles(profileDataDir)
            : GetChromiumProfiles(profileDataDir);
    }

    private static List<BrowserProfile> GetChromiumProfiles(string userDataDir)
    {
        var profiles = new List<BrowserProfile>();
        if (string.IsNullOrEmpty(userDataDir) || !Directory.Exists(userDataDir))
            return profiles;

        string localStatePath = Path.Combine(userDataDir, "Local State");
        if (File.Exists(localStatePath))
        {
            try
            {
                string json = File.ReadAllText(localStatePath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("profile", out var profileRoot) &&
                    profileRoot.TryGetProperty("info_cache", out var infoCache))
                {
                    foreach (var entry in infoCache.EnumerateObject())
                    {
                        string dirName = entry.Name;
                        string displayName = dirName;
                        if (entry.Value.TryGetProperty("name", out var nameProp))
                            displayName = nameProp.GetString() ?? dirName;

                        if (Directory.Exists(Path.Combine(userDataDir, dirName)))
                            profiles.Add(new BrowserProfile(dirName, displayName));
                    }
                }
            }
            catch { }
        }

        if (profiles.Count == 0)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(userDataDir))
                {
                    if (File.Exists(Path.Combine(dir, "Preferences")))
                    {
                        string dirName = Path.GetFileName(dir);
                        profiles.Add(new BrowserProfile(dirName, dirName));
                    }
                }
            }
            catch { }
        }

        return profiles.OrderBy(p => p.DirectoryName != "Default")
                       .ThenBy(p => p.DisplayName)
                       .ToList();
    }

    private static List<BrowserProfile> GetGeckoProfiles(string profileDataDir)
    {
        var profiles = new List<BrowserProfile>();
        string iniPath = Path.Combine(profileDataDir, "profiles.ini");
        if (!File.Exists(iniPath))
            return profiles;

        try
        {
            string? currentName = null;
            string? currentPath = null;
            bool isRelative = true;

            foreach (string rawLine in File.ReadLines(iniPath))
            {
                string line = rawLine.Trim();

                if (line.StartsWith('['))
                {
                    if (currentPath != null)
                    {
                        string fullPath = isRelative
                            ? Path.Combine(profileDataDir, currentPath.Replace('/', '\\'))
                            : currentPath;

                        if (Directory.Exists(fullPath))
                            profiles.Add(new BrowserProfile(fullPath, currentName ?? Path.GetFileName(fullPath)));
                    }

                    currentName = null;
                    currentPath = null;
                    isRelative = true;

                    if (!line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase))
                        currentPath = null;

                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();

                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    currentName = value;
                else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                    currentPath = value;
                else if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase))
                    isRelative = value == "1";
            }

            if (currentPath != null)
            {
                string fullPath = isRelative
                    ? Path.Combine(profileDataDir, currentPath.Replace('/', '\\'))
                    : currentPath;

                if (Directory.Exists(fullPath))
                    profiles.Add(new BrowserProfile(fullPath, currentName ?? Path.GetFileName(fullPath)));
            }
        }
        catch { }

        return profiles;
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is FrameworkElement fe && fe.Tag is LauncherItem item)
        {
            var items = CurrentItems;
            if (!items.Remove(item))
            {
                foreach (var group in items.Where(i => i.IsGroup))
                    if (group.Children.Remove(item)) break;
            }
            SaveAndUpdateTaskbar();
        }
    }

    private ObservableCollection<LauncherItem>? FindParentCollection(LauncherItem item)
    {
        var items = CurrentItems;
        if (items.Contains(item)) return items;
        foreach (var group in items.Where(i => i.IsGroup))
            if (group.Children.Contains(item)) return group.Children;
        return null;
    }

    private void MoveItemUp_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is not FrameworkElement fe || fe.Tag is not LauncherItem item) return;
        var parent = FindParentCollection(item);
        if (parent == null) return;
        int index = parent.IndexOf(item);
        if (index <= 0) return;
        parent.Move(index, index - 1);
        SaveAndUpdateTaskbar();
    }

    private void MoveItemDown_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly) return;
        if (sender is not FrameworkElement fe || fe.Tag is not LauncherItem item) return;
        var parent = FindParentCollection(item);
        if (parent == null) return;
        int index = parent.IndexOf(item);
        if (index < 0 || index >= parent.Count - 1) return;
        parent.Move(index, index + 1);
        SaveAndUpdateTaskbar();
    }

    private record InstalledApp(string DisplayName, string ExePath);

    private record InstalledPwa(string DisplayName, string Aumid, string Domain);

    /// <summary>
    /// Discovers installed Progressive Web Apps by enumerating shell:AppsFolder
    /// for Chromium-based PWA entries (registered with AUMIDs like domain-HEX_hash!App).
    /// </summary>
    private static List<InstalledPwa> GetInstalledPwas()
    {
        var pwas = new List<InstalledPwa>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return pwas;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder == null) return pwas;

            foreach (dynamic item in folder.Items())
            {
                string? path = item.Path as string;
                string? name = item.Name as string;
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) continue;
                if (!path.EndsWith("!App", StringComparison.Ordinal)) continue;

                // Chromium PWA AUMIDs: {domain}-{HEX}_{hash}!App
                var match = System.Text.RegularExpressions.Regex.Match(path,
                    @"^([\w][\w.-]*\.[a-zA-Z]{2,})-[A-Fa-f0-9]+_[a-z0-9]+!App$");
                if (!match.Success) continue;

                string domain = match.Groups[1].Value;
                pwas.Add(new InstalledPwa(name, path, domain));
            }

            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(folder);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        catch { }

        return pwas.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Builds a list of installed applications by scanning Start Menu shortcuts
    /// and the Windows Registry uninstall keys.
    /// </summary>
    private static List<InstalledApp> GetInstalledApplications()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        var startMenuDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        foreach (var dir in startMenuDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        var target = ResolveShortcutTarget(lnk);
                        if (string.IsNullOrEmpty(target)) continue;
                        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(target)) continue;

                        var name = Path.GetFileNameWithoutExtension(lnk);
                        if (IsNonAppName(name)) continue;

                        if (!apps.ContainsKey(target))
                            apps[target] = new InstalledApp(name, target);
                    }
                    catch { }
                }
            }
            catch { }
        }

        var uninstallKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var keyPath in uninstallKeys)
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using var key = root.OpenSubKey(keyPath);
                    if (key == null) continue;

                    foreach (var subName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = key.OpenSubKey(subName);
                            if (sub == null) continue;

                            var systemComponent = sub.GetValue("SystemComponent");
                            if (systemComponent is int sc && sc == 1) continue;
                            var parentName = sub.GetValue("ParentDisplayName") as string;
                            if (!string.IsNullOrEmpty(parentName)) continue;
                            var releaseType = sub.GetValue("ReleaseType") as string;
                            if (!string.IsNullOrEmpty(releaseType)) continue;

                            var displayName = sub.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName)) continue;
                            if (IsNonAppName(displayName)) continue;

                            var exePath = ResolveAppExePath(sub);
                            if (string.IsNullOrEmpty(exePath)) continue;

                            if (!apps.ContainsKey(exePath))
                                apps[exePath] = new InstalledApp(displayName, exePath);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // Also enumerate shell:AppsFolder for Store/packaged apps
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                if (folder != null)
                {
                    var pwaPattern = new System.Text.RegularExpressions.Regex(
                        @"^[\w][\w.-]*\.[a-zA-Z]{2,}-[A-Fa-f0-9]+_[a-z0-9]+!App$");

                    // Build a set of display names already found via Start Menu / Registry
                    // so we don't duplicate them with shell:AppsFolder entries.
                    var existingNames = new HashSet<string>(
                        apps.Values.Select(a => a.DisplayName),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (dynamic item in folder.Items())
                    {
                        string? path = item.Path as string;
                        string? name = item.Name as string;
                        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) continue;

                        // Skip Chromium PWAs (handled by the PWA picker)
                        if (pwaPattern.IsMatch(path)) continue;

                        if (IsNonAppName(name)) continue;

                        // Skip if already discovered via Start Menu / Registry
                        if (existingNames.Contains(name)) continue;

                        string launchPath = $"shell:AppsFolder\\{path}";
                        if (!apps.ContainsKey(launchPath))
                            apps[launchPath] = new InstalledApp(name, launchPath);
                    }

                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(folder);
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                }
            }
        }
        catch { }

        return apps.Values
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsNonAppName(string name)
    {
        string[] filters =
        [
            "SDK", "Runtime", "Redistributable", "Targeting Pack", "Manifest",
            "Toolset", "Template", "Hosting Bundle", "AppHost", "SharedHost",
            "WindowsDesktop", "Host (", "- Debug", "IntelliTrace",
            "DiagnosticsHub", "IntelliSense", "Language Pack",
            "Driver", "Firmware", "BIOS", "Chipset",
            ".NET Framework", "Microsoft .NET", "Microsoft ASP.NET",
            "Microsoft Windows Desktop", "Microsoft Visual C++",
            "Uninstall"
        ];

        foreach (var filter in filters)
        {
            if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (name.StartsWith('{') || name.StartsWith("KB"))
            return true;

        return false;
    }

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string? target = shortcut.TargetPath;
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveAppExePath(Microsoft.Win32.RegistryKey sub)
    {
        var displayIcon = sub.GetValue("DisplayIcon") as string;
        if (!string.IsNullOrEmpty(displayIcon))
        {
            var iconPath = displayIcon.Split(',')[0].Trim('"', ' ');
            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
                return iconPath;
        }

        var installLoc = sub.GetValue("InstallLocation") as string;
        if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
        {
            var displayName = sub.GetValue("DisplayName") as string ?? "";
            foreach (var exe in Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var fn = Path.GetFileNameWithoutExtension(exe);
                if (displayName.Contains(fn, StringComparison.OrdinalIgnoreCase)
                    || fn.Contains(displayName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return exe;
            }
            var firstExe = Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (firstExe != null) return firstExe;
        }

        return null;
    }

    private void SaveAndUpdateTaskbar()
    {
        SettingsManager.SaveSettings();
        Services.AutoSyncService.NotifyItemsChanged();
        FlyoutWindow.InvalidateItems(TargetLauncher?.Id);
        RebuildColumns();
    }

    private async void ExportItems_Click(object sender, RoutedEventArgs e)
    {
        var items = CurrentItems;
        if (items.Count == 0) return;

        var picker = new FileSavePicker();
        InitializePicker(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "launcher-items";
        picker.FileTypeChoices.Add("JSON files", [".json"]);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var list = new List<LauncherItem>(items);
            string json = JsonSerializer.Serialize(list, SettingsManager.JsonOptions);
            await File.WriteAllTextAsync(file.Path, json);
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Export Failed",
                Content = ex.Message,
                CloseButtonText = "OK"
            };
            await dialog.ShowAsync();
        }
    }

    private async void ImportItems_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".xml");

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        List<LauncherItem>? imported;
        try
        {
            if (file.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var serializer = new XmlSerializer(typeof(List<LauncherItem>));
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read);
                imported = serializer.Deserialize(stream) as List<LauncherItem>;
            }
            else
            {
                string text = await File.ReadAllTextAsync(file.Path);
                imported = JsonSerializer.Deserialize<List<LauncherItem>>(text, SettingsManager.JsonOptions);
            }
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Import Failed",
                Content = $"Could not read the file: {ex.Message}",
                CloseButtonText = "OK"
            };
            await dialog.ShowAsync();
            return;
        }

        if (imported == null || imported.Count == 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Import",
                Content = "The file contained no items.",
                CloseButtonText = "OK"
            };
            await dialog.ShowAsync();
            return;
        }

        // Ask user whether to replace or merge
        var modeDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Import Items",
            Content = $"Found {imported.Count} item(s). Replace all existing items or add to the current list?",
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Add",
            CloseButtonText = "Cancel"
        };

        var result = await modeDialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        var collection = CurrentItems;

        if (result == ContentDialogResult.Primary)
            collection.Clear();

        foreach (var item in imported)
        {
            item.NormalizeGlyph();
            collection.Add(item);
        }

        SaveAndUpdateTaskbar();

        // Fetch missing icons for imported items — IconPath changes fire INPC so bindings update automatically
        await FaviconService.FetchMissingItemIconsAsync(imported);
        SettingsManager.SaveSettings();

        // Invalidate the flyout so it picks up new items + icons
        FlyoutWindow.InvalidateItems();
    }

    // -- Bookmark import --

    /// <summary>Wraps a bookmark label string so TreeView can display it via ToString()
    /// and we can use reference equality for the ItemInvoked reverse lookup.</summary>
    private sealed class BookmarkLabel(string text)
    {
        public override string ToString() => text;
    }

    /// <summary>Represents a node in a browser bookmark tree — either a folder or a URL bookmark.</summary>
    private sealed class BookmarkNode
    {
        public string Name { get; init; } = "";
        public string? Url { get; init; }   // null for folders
        public List<BookmarkNode> Children { get; } = [];
        public bool IsFolder => Url == null;

        public int CountLeaves()
        {
            if (!IsFolder) return 1;
            return Children.Sum(c => c.CountLeaves());
        }
    }

    /// <summary>Reads bookmarks from a Chromium profile directory, returning a root folder tree.</summary>
    private static List<BookmarkNode> ReadChromiumBookmarks(string profileDir)
    {
        var result = new List<BookmarkNode>();
        string path = Path.Combine(profileDir, "Bookmarks");
        // Newer Chrome versions store bookmarks in "AccountBookmarks" instead of "Bookmarks"
        if (!File.Exists(path))
            path = Path.Combine(profileDir, "AccountBookmarks");
        if (!File.Exists(path)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("roots", out var roots)) return result;

            static BookmarkNode? ParseNode(JsonElement el)
            {
                if (!el.TryGetProperty("type", out var tp)) return null;
                string type = tp.GetString() ?? "";
                string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                if (type == "url")
                {
                    string url = el.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(url) ||
                        url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                        return null;
                    return new BookmarkNode { Name = string.IsNullOrEmpty(name) ? url : name, Url = url };
                }
                if (type == "folder")
                {
                    var folder = new BookmarkNode { Name = name };
                    if (el.TryGetProperty("children", out var children))
                        foreach (var child in children.EnumerateArray())
                        {
                            var parsed = ParseNode(child);
                            if (parsed != null) folder.Children.Add(parsed);
                        }
                    return folder;
                }
                return null;
            }

            var rootSections = new[] {
                ("bookmark_bar", "Bookmarks Bar"),
                ("other",        "Other Bookmarks"),
                ("synced",       "Synced Bookmarks"),
            };
            foreach (var (key, displayName) in rootSections)
            {
                if (!roots.TryGetProperty(key, out var rootEl)) continue;
                var parsed = ParseNode(rootEl);
                if (parsed == null || parsed.CountLeaves() == 0) continue;
                // Wrap using a friendly top-level name instead of the browser's internal label
                var container = new BookmarkNode { Name = displayName };
                foreach (var child in parsed.Children) container.Children.Add(child);
                result.Add(container);
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Reads bookmarks from a Firefox/Gecko profile directory using the most recent
    /// auto-generated jsonlz4 backup, falling back to places.sqlite if no backups exist.
    /// </summary>
    private static List<BookmarkNode> ReadGeckoBookmarks(string profileDir)
    {
        var result = new List<BookmarkNode>();

        // Try jsonlz4 backup first
        string backupDir = Path.Combine(profileDir, "bookmarkbackups");
        string? latestFile = Directory.Exists(backupDir)
            ? Directory.GetFiles(backupDir, "*.jsonlz4")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        // Fall back to places.sqlite when no jsonlz4 backups exist
        if (latestFile == null)
            return ReadGeckoBookmarksFromSqlite(profileDir);


        try
        {
            byte[] raw = File.ReadAllBytes(latestFile);
            // mozLz40\0 magic (8 bytes) + original size little-endian int32 (4 bytes) + LZ4 block data
            if (raw.Length < 12 ||
                raw[0] != 'm' || raw[1] != 'o' || raw[2] != 'z' || raw[3] != 'L' ||
                raw[4] != 'z' || raw[5] != '4' || raw[6] != '0' || raw[7] != 0)
                return result;

            int origSize = BitConverter.ToInt32(raw, 8);
            if (origSize <= 0 || origSize > 64 * 1024 * 1024) return result;

            byte[] json = DecompressLz4Block(raw.AsSpan(12), origSize);
            using var doc = JsonDocument.Parse(json);

            static BookmarkNode? ParseGeckoNode(JsonElement el)
            {
                if (!el.TryGetProperty("type", out var tp)) return null;
                string type = tp.GetString() ?? "";
                string name = el.TryGetProperty("title", out var n) ? n.GetString() ?? "" : "";

                if (type == "text/x-moz-place")
                {
                    string uri = el.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(uri) ||
                        uri.StartsWith("place:", StringComparison.OrdinalIgnoreCase) ||
                        uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                        return null;
                    return new BookmarkNode { Name = string.IsNullOrEmpty(name) ? uri : name, Url = uri };
                }
                if (type == "text/x-moz-place-container")
                {
                    var folder = new BookmarkNode { Name = name };
                    if (el.TryGetProperty("children", out var children))
                        foreach (var child in children.EnumerateArray())
                        {
                            var parsed = ParseGeckoNode(child);
                            if (parsed != null) folder.Children.Add(parsed);
                        }
                    return folder;
                }
                return null;  // separator or unrecognised type
            }

            // Root is the placesRoot container; its direct children are the top-level sections
            if (doc.RootElement.TryGetProperty("children", out var topChildren))
                foreach (var child in topChildren.EnumerateArray())
                {
                    var node = ParseGeckoNode(child);
                    if (node != null && node.CountLeaves() > 0)
                        result.Add(node);
                }
        }
        catch { }

        return result;
    }

    /// <summary>Decompresses an LZ4 block-format payload (no frame header).</summary>
    private static byte[] DecompressLz4Block(ReadOnlySpan<byte> src, int outputSize)
    {
        var dst = new byte[outputSize];
        int sPos = 0, dPos = 0;

        while (sPos < src.Length && dPos < outputSize)
        {
            byte token = src[sPos++];

            // Literals
            int litLen = (token >> 4) & 0xF;
            if (litLen == 15)
            {
                byte extra;
                do { extra = src[sPos++]; litLen += extra; } while (extra == 255);
            }
            src.Slice(sPos, litLen).CopyTo(dst.AsSpan(dPos));
            sPos += litLen;
            dPos += litLen;

            if (sPos >= src.Length) break;  // final sequence has no match portion

            // Match offset (little-endian 16-bit)
            int offset = src[sPos] | (src[sPos + 1] << 8);
            sPos += 2;

            // Match length: base is 4, plus token low nibble, plus optional extension bytes
            int matchLen = 4 + (token & 0xF);
            if ((token & 0xF) == 15)
            {
                byte extra;
                do { extra = src[sPos++]; matchLen += extra; } while (extra == 255);
            }

            // Copy match — may overlap with current write position, so byte-by-byte
            int matchSrc = dPos - offset;
            for (int i = 0; i < matchLen; i++)
                dst[dPos++] = dst[matchSrc++];
        }

        return dst;
    }

    /// <summary>Reads bookmarks from a Firefox/Gecko places.sqlite database.</summary>
    private static List<BookmarkNode> ReadGeckoBookmarksFromSqlite(string profileDir)
    {
        var result = new List<BookmarkNode>();
        string dbPath = Path.Combine(profileDir, "places.sqlite");
        if (!File.Exists(dbPath)) return result;

        // Copy to temp to avoid SQLite lock conflicts with a running browser
        string tempDb = Path.Combine(Path.GetTempPath(), $"ll_places_{Guid.NewGuid():N}.sqlite");
        try
        {
            File.Copy(dbPath, tempDb, overwrite: true);

            using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly");
            conn.Open();

            // Build folder hierarchy from moz_bookmarks + moz_places
            // type=1 → bookmark, type=2 → folder; parent links form the tree
            // Well-known root folder IDs: 1=Places root, 2=Bookmarks Menu, 3=Toolbar, 4=Tags, 5=Other Bookmarks
            var folders = new Dictionary<long, BookmarkNode>();
            var childrenMap = new Dictionary<long, List<(long id, int type, string title, string? url, long parent)>>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT b.id, b.type, COALESCE(b.title, ''), p.url, b.parent, b.position
                    FROM moz_bookmarks b
                    LEFT JOIN moz_places p ON b.fk = p.id
                    WHERE b.type IN (1, 2)
                    ORDER BY b.position
                    """;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    int type = reader.GetInt32(1);
                    string title = reader.GetString(2);
                    string? url = reader.IsDBNull(3) ? null : reader.GetString(3);
                    long parent = reader.GetInt64(4);

                    if (type == 2) // folder
                        folders[id] = new BookmarkNode { Name = title };

                    if (!childrenMap.TryGetValue(parent, out var list))
                    {
                        list = [];
                        childrenMap[parent] = list;
                    }
                    list.Add((id, type, title, url, parent));
                }
            }

            // Recursively build tree
            void BuildChildren(BookmarkNode parentNode, long parentId)
            {
                if (!childrenMap.TryGetValue(parentId, out var children)) return;
                foreach (var (id, type, title, url, _) in children)
                {
                    if (type == 2 && folders.TryGetValue(id, out var folder))
                    {
                        BuildChildren(folder, id);
                        if (folder.CountLeaves() > 0)
                            parentNode.Children.Add(folder);
                    }
                    else if (type == 1 && !string.IsNullOrEmpty(url))
                    {
                        if (url.StartsWith("place:", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string name = string.IsNullOrEmpty(title) ? url : title;
                        parentNode.Children.Add(new BookmarkNode { Name = name, Url = url });
                    }
                }
            }

            // Well-known top-level folders under the Places root (id=1)
            var topLevel = new (long id, string displayName)[]
            {
                (2, "Bookmarks Menu"),
                (3, "Bookmarks Toolbar"),
                (5, "Other Bookmarks"),
            };

            foreach (var (folderId, displayName) in topLevel)
            {
                if (!folders.TryGetValue(folderId, out var folder)) continue;
                folder = new BookmarkNode { Name = displayName };
                BuildChildren(folder, folderId);
                if (folder.CountLeaves() > 0)
                    result.Add(folder);
            }
        }
        catch { }
        finally
        {
            try { File.Delete(tempDb); } catch { }
        }

        return result;
    }

    private async void ImportBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var allBrowsers = GetInstalledBrowsers();

        if (allBrowsers.Count == 0)
        {
            var d = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "No Supported Browsers Found",
                Content = "No supported browsers were found. Bookmark import supports Microsoft Edge, Google Chrome, Brave, Vivaldi, Chromium, Firefox, Zen, Waterfox, and LibreWolf.",
                CloseButtonText = "OK"
            };
            await d.ShowAsync();
            return;
        }

        KnownBrowser? lastBrowser = null;
        BrowserProfile? lastProfile = null;

        while (true)
        {
            var step1 = await ShowBookmarkBrowserPickerAsync(allBrowsers, lastBrowser, lastProfile);
            if (step1 == null) return;
            (lastBrowser, lastProfile) = step1.Value;

            // Gecko profiles store the full profile path in DirectoryName; Chromium stores a relative subdirectory name
            string profileDir = lastBrowser.Engine == BrowserEngine.Gecko
                ? lastProfile.DirectoryName
                : Path.Combine(lastBrowser.ProfileDataDir, lastProfile.DirectoryName);

            var bookmarkRoots = lastBrowser.Engine == BrowserEngine.Gecko
                ? ReadGeckoBookmarks(profileDir)
                : ReadChromiumBookmarks(profileDir);

            if (bookmarkRoots.Count == 0 || bookmarkRoots.Sum(r => r.CountLeaves()) == 0)
            {
                var d = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "No Bookmarks Found",
                    Content = $"No web bookmarks were found in the selected {lastBrowser.DisplayName} profile.",
                    CloseButtonText = "OK"
                };
                await d.ShowAsync();
                continue;
            }

            var (selected, goBack) = await ShowBookmarkSelectorAsync(bookmarkRoots);
            if (goBack) continue;
            if (selected == null || selected.Count == 0) return;

            var newItems = selected
                .Select(b => new LauncherItem { Name = b.Name, Path = b.Url!, IsWebsite = true })
                .ToList();

            foreach (var item in newItems)
                item.NormalizeGlyph();

            foreach (var item in newItems)
                CurrentItems.Add(item);

            SaveAndUpdateTaskbar();
            await FaviconService.FetchMissingItemIconsAsync(newItems);
            SettingsManager.SaveSettings();
            FlyoutWindow.InvalidateItems();
            return;
        }
    }

    /// <summary>
    /// Shows a dialog for the user to pick a browser and profile.
    /// Returns null if the user cancels. Pre-populates selections when returning via Back.
    /// </summary>
    private async Task<(KnownBrowser Browser, BrowserProfile Profile)?> ShowBookmarkBrowserPickerAsync(
        List<KnownBrowser> browsers, KnownBrowser? defaultBrowser, BrowserProfile? defaultProfile)
    {
        var browserCombo = new ComboBox
        {
            PlaceholderText = "Select browser",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var b in browsers)
            browserCombo.Items.Add(b.DisplayName);

        var profileCombo = new ComboBox
        {
            PlaceholderText = "Select profile",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false
        };

        List<BrowserProfile> currentProfiles = [];

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Import Browser Bookmarks",
            PrimaryButtonText = "Next",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };

        void PopulateProfiles()
        {
            profileCombo.Items.Clear();
            profileCombo.IsEnabled = false;
            if (browserCombo.SelectedIndex < 0) { currentProfiles = []; return; }

            var browser = browsers[browserCombo.SelectedIndex];
            currentProfiles = GetBrowserProfiles(browser.ProfileDataDir, browser.Engine);
            if (currentProfiles.Count == 0)
            {
                profileCombo.PlaceholderText = "No profiles found";
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }
            foreach (var p in currentProfiles)
                profileCombo.Items.Add(p.DisplayName);
            profileCombo.IsEnabled = true;
            profileCombo.SelectedIndex = 0;
        }

        browserCombo.SelectionChanged += (_, _) =>
        {
            PopulateProfiles();
            dialog.IsPrimaryButtonEnabled = profileCombo.SelectedIndex >= 0;
        };
        profileCombo.SelectionChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = browserCombo.SelectedIndex >= 0 && profileCombo.SelectedIndex >= 0;
        };

        // Pre-select defaults when the user navigates Back from the bookmark picker
        if (defaultBrowser != null)
        {
            int idx = browsers.FindIndex(b => b.DisplayName == defaultBrowser.DisplayName);
            if (idx >= 0)
            {
                browserCombo.SelectedIndex = idx; // fires SelectionChanged → PopulateProfiles()
                if (defaultProfile != null)
                {
                    int pidx = currentProfiles.FindIndex(p => p.DirectoryName == defaultProfile.DirectoryName);
                    if (pidx >= 0) profileCombo.SelectedIndex = pidx;
                }
            }
        }

        var browserGroup = new StackPanel { Spacing = 4 };
        browserGroup.Children.Add(new TextBlock { Text = "Browser", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        browserGroup.Children.Add(browserCombo);

        var profileGroup = new StackPanel { Spacing = 4 };
        profileGroup.Children.Add(new TextBlock { Text = "Profile", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        profileGroup.Children.Add(profileCombo);

        var content = new StackPanel { Spacing = 16, MinWidth = 380 };
        content.Children.Add(new TextBlock
        {
            Text = "Select a browser and profile to import bookmarks from.",
            TextWrapping = TextWrapping.WrapWholeWords,
            Opacity = 0.7
        });
        content.Children.Add(browserGroup);
        content.Children.Add(profileGroup);

        dialog.Content = content;

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        if (browserCombo.SelectedIndex < 0 || profileCombo.SelectedIndex < 0) return null;

        return (browsers[browserCombo.SelectedIndex], currentProfiles[profileCombo.SelectedIndex]);
    }

    /// <summary>
    /// Shows a TreeView dialog for selecting bookmarks from a folder hierarchy.
    /// Returns (selected URL nodes, goBack=false) on confirm, (null, goBack=true) on Back,
    /// or (null, goBack=false) on Cancel.
    /// </summary>
    private async Task<(List<BookmarkNode>? Selected, bool GoBack)> ShowBookmarkSelectorAsync(
        List<BookmarkNode> roots)
    {
        var nodeMap = new Dictionary<TreeViewNode, BookmarkNode>();
        var contentToNode = new Dictionary<BookmarkLabel, TreeViewNode>();

        TreeViewNode MakeNode(BookmarkNode bm, bool isRoot = false)
        {
            string text = bm.IsFolder
                ? $"{bm.Name}  ({bm.CountLeaves()})"
                : bm.Name;
            var label = new BookmarkLabel(text);
            var node = new TreeViewNode { Content = label, IsExpanded = isRoot };
            nodeMap[node] = bm;
            contentToNode[label] = node;
            foreach (var child in bm.Children)
                node.Children.Add(MakeNode(child));
            return node;
        }

        var treeView = new TreeView
        {
            SelectionMode = TreeViewSelectionMode.Multiple,
            MaxHeight = 420
        };

        int totalCount = roots.Sum(r => r.CountLeaves());
        foreach (var root in roots)
            treeView.RootNodes.Add(MakeNode(root, isRoot: true));

        // Clicking a folder name toggles expand/collapse
        treeView.ItemInvoked += (_, args) =>
        {
            if (args.InvokedItem is BookmarkLabel invokedLabel &&
                contentToNode.TryGetValue(invokedLabel, out var node) &&
                nodeMap.TryGetValue(node, out var bm) && bm.IsFolder)
            {
                node.IsExpanded = !node.IsExpanded;
            }
        };

        // Flat list of all nodes for Select All / Deselect All
        var allNodes = new List<TreeViewNode>();
        void CollectNodes(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes) { allNodes.Add(n); CollectNodes(n.Children); }
        }
        CollectNodes(treeView.RootNodes);

        var selectedCountText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            FontSize = 13,
            Text = "None selected"
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Select Bookmarks ({totalCount})",
            PrimaryButtonText = "Import",
            SecondaryButtonText = "← Back",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };

        bool batchUpdating = false;

        void UpdateCount()
        {
            int count = treeView.SelectedNodes.Count(n => nodeMap.TryGetValue(n, out var bm) && !bm.IsFolder);
            selectedCountText.Text = count > 0 ? $"{count} selected" : "None selected";
            dialog.IsPrimaryButtonEnabled = count > 0;
            dialog.PrimaryButtonText = count > 0 ? $"Import {count}" : "Import";
        }

        treeView.SelectionChanged += (_, _) => { if (!batchUpdating) UpdateCount(); };

        var selectAllButton = new HyperlinkButton { Content = "Select All", Padding = new Thickness(0) };
        var deselectAllButton = new HyperlinkButton { Content = "Deselect All", Padding = new Thickness(0) };

        selectAllButton.Click += (_, _) =>
        {
            batchUpdating = true;
            treeView.SelectedNodes.Clear();
            foreach (var n in allNodes) treeView.SelectedNodes.Add(n);
            batchUpdating = false;
            UpdateCount();
        };
        deselectAllButton.Click += (_, _) =>
        {
            batchUpdating = true;
            treeView.SelectedNodes.Clear();
            batchUpdating = false;
            UpdateCount();
        };

        var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolRow.Children.Add(selectAllButton);
        toolRow.Children.Add(new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.4 });
        toolRow.Children.Add(deselectAllButton);
        toolRow.Children.Add(new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.4 });
        toolRow.Children.Add(selectedCountText);

        var content = new StackPanel { Spacing = 8, MinWidth = 480 };
        content.Children.Add(toolRow);
        content.Children.Add(treeView);

        dialog.Content = content;

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Secondary) return (null, true);   // Back
        if (result != ContentDialogResult.Primary) return (null, false);    // Cancel

        var selected = treeView.SelectedNodes
            .Where(n => nodeMap.TryGetValue(n, out var bm) && !bm.IsFolder)
            .Select(n => nodeMap[n])
            .ToList();

        return (selected, false);
    }
}
