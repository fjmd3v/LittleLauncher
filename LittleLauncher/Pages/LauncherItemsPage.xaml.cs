using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Services;
using LittleLauncher.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.IO;
using System.Xml.Serialization;
using global::Windows.Storage.Pickers;
using WinRT.Interop;
using Image = Microsoft.UI.Xaml.Controls.Image;

namespace LittleLauncher.Pages;

/// <summary>
/// Selects the correct DataTemplate based on item type: group, heading, or launchable item.
/// Shared groups are routed to the owner or subscriber template based on UserSettings.
/// </summary>
public class LauncherItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? LauncherItemTemplate { get; set; }
    public DataTemplate? GroupItemTemplate { get; set; }
    public DataTemplate? ColumnBreakItemTemplate { get; set; }
    public DataTemplate? SharedGroupOwnerTemplate { get; set; }
    public DataTemplate? SharedGroupSubscriberTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is LauncherItem li)
        {
            if (li.IsGroup)
            {
                if (!string.IsNullOrEmpty(li.SharedGroupId))
                {
                    bool isOwner = SettingsManager.Current.SharedGroupSources
                        .Any(s => s.Id == li.SharedGroupId && s.IsOwner);
                    return isOwner ? SharedGroupOwnerTemplate : SharedGroupSubscriberTemplate;
                }
                return GroupItemTemplate;
            }
            if (li.IsColumnBreak) return ColumnBreakItemTemplate;
        }
        return LauncherItemTemplate;
    }
}

/// <summary>
/// Selects read-only item templates for children inside subscribed (read-only) shared groups.
/// </summary>
public class ReadOnlyItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? LauncherItemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return LauncherItemTemplate;
    }
}

public partial class LauncherItemsPage : Page
{
    /// <summary>
    /// When set, the edit dialog for this item opens automatically after the page loads.
    /// </summary>
    internal static LauncherItem? PendingEditItem { get; set; }

    // -- Cross-list drag-and-drop tracking --
    private LauncherItem? _dragItem;
    private ObservableCollection<LauncherItem>? _dragSourceCollection;
    private ListViewItem? _lastIndicatorContainer;

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

    public LauncherItemsPage()
    {
        InitializeComponent();
        RefreshList();
        Loaded += LauncherItemsPage_Loaded;
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

    private void RefreshList()
    {
        ItemsList.ItemsSource = null;
        ItemsList.ItemsSource = SettingsManager.Current.LauncherItems;
    }

    /// <summary>
    /// Refreshes the settings page ListView if the page is currently loaded.
    /// Called from FlyoutWindow after drag-reorder.
    /// </summary>
    internal static void NotifyItemsChanged()
    {
        var settingsWindow = SettingsWindow.GetCurrent();
        if (settingsWindow?.CurrentPage is LauncherItemsPage page)
            page.RefreshList();
    }

    private void ItemsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is LauncherItem item)
        {
            _dragItem = item;
            _dragSourceCollection = SettingsManager.Current.LauncherItems;
            e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        }
    }

    private void ItemsList_DragOver(object sender, DragEventArgs e)
    {
        if (_dragItem == null || _dragSourceCollection == null) return;

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        // Show insertion indicator
        int dropIndex = GetDropIndex(ItemsList, e);
        ShowInsertionIndicator(ItemsList, dropIndex);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        if (dropIndex < SettingsManager.Current.LauncherItems.Count)
        {
            var targetItem = SettingsManager.Current.LauncherItems[dropIndex];
            e.DragUIOverride.Caption = $"Move above {targetItem.Name}";
        }
        else
        {
            e.DragUIOverride.Caption = "Move to end";
        }

        e.Handled = true;
    }

    private void ItemsList_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
    }

    private void ItemsList_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        if (_dragItem == null || _dragSourceCollection == null) return;

        var items = SettingsManager.Current.LauncherItems;
        int dropIndex = GetDropIndex(ItemsList, e);

        // If reordering within the same list, adjust for the removal shift
        int originalIndex = _dragSourceCollection == items ? _dragSourceCollection.IndexOf(_dragItem) : -1;
        _dragSourceCollection.Remove(_dragItem);
        if (originalIndex >= 0 && originalIndex < dropIndex)
            dropIndex--;
        if (dropIndex > items.Count) dropIndex = items.Count;
        items.Insert(dropIndex, _dragItem);

        _dragItem = null;
        _dragSourceCollection = null;

        SaveAndUpdateTaskbar();
        e.Handled = true;
    }

    private void ItemsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        TopLevelDropZone.Visibility = Visibility.Collapsed;
        _dragItem = null;
        _dragSourceCollection = null;
    }

    private void GroupRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel groupRoot) return;
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

    private void ItemCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border && FindGripIcon(border) is FontIcon grip)
            grip.Opacity = 0.8;
        this.ProtectedCursor = _sizeAllCursor;
    }

    private void ItemCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border && FindGripIcon(border) is FontIcon grip)
            grip.Opacity = 0.3;
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

    private void MoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not LauncherItem item) return;

        var items = SettingsManager.Current.LauncherItems;

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
        await ShowItemDialog(null);
    }

    private async void ShowAddGroupDialog_Click(object sender, RoutedEventArgs e)
    {
        await ShowGroupDialog(null);
    }

    private void ShowAddColumnBreakDialog_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.LauncherItems.Add(LauncherItem.CreateColumnBreak());
        SaveAndUpdateTaskbar();
    }

    private async void EditItem_Click(object sender, RoutedEventArgs e)
    {
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

        // Check if this is an owned shared group
        SharedGroupSource? sharedSource = null;
        if (isEdit && !string.IsNullOrEmpty(existingItem!.SharedGroupId))
        {
            sharedSource = SettingsManager.Current.SharedGroupSources
                .FirstOrDefault(s => s.Id == existingItem.SharedGroupId && s.IsOwner);
        }

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

        // Sharing info section for owned shared groups
        if (sharedSource != null)
        {
            var separator = new Border
            {
                Height = 1,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                Margin = new Thickness(0, 12, 0, 12)
            };
            form.Children.Add(separator);

            form.Children.Add(new TextBlock
            {
                Text = "Sharing",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 6)
            });
            form.Children.Add(new TextBlock
            {
                Text = "This group is being shared to:",
                FontSize = 13,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 4)
            });
            form.Children.Add(new TextBlock
            {
                Text = sharedSource.GetLocationDescription(),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 0)
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = isEdit ? "Edit Group" : "Add Group",
            Content = form,
            PrimaryButtonText = isEdit ? "Save" : "Add",
            SecondaryButtonText = sharedSource != null ? "Stop Sharing" : "",
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

        if (result == ContentDialogResult.Secondary && sharedSource != null)
        {
            // Stop sharing: unlink the group and remove the source
            existingItem!.SharedGroupId = string.Empty;
            SettingsManager.Current.SharedGroupSources.Remove(sharedSource);
            RefreshList();
            SaveAndUpdateTaskbar();
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();

        if (isEdit)
        {
            existingItem!.Name = name;
        }
        else
        {
            SettingsManager.Current.LauncherItems.Add(LauncherItem.CreateGroup(name));
        }

        SaveAndUpdateTaskbar();
    }

    private async void ShowAddItemToGroup_Click(object sender, RoutedEventArgs e)
    {
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
            e.DragUIOverride.Caption = $"Move above {group.Children[dropIndex].Name}";
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

        SaveAndUpdateTaskbar();
        e.Handled = true;
    }

    private void GroupChildList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        TopLevelDropZone.Visibility = Visibility.Collapsed;
        _dragItem = null;
        _dragSourceCollection = null;
    }

    // -- Top-level drop zone (visible when dragging from a group) --

    private void TopLevelDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (_dragItem == null || _dragSourceCollection == null) return;
        if (_dragSourceCollection == SettingsManager.Current.LauncherItems) return;

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
        if (_dragItem == null || _dragSourceCollection == null) return;
        if (_dragSourceCollection == SettingsManager.Current.LauncherItems) return;

        var items = SettingsManager.Current.LauncherItems;
        _dragSourceCollection.Remove(_dragItem);
        items.Add(_dragItem);

        _dragItem = null;
        _dragSourceCollection = null;
        TopLevelDropZone.Visibility = Visibility.Collapsed;

        SaveAndUpdateTaskbar();
        e.Handled = true;
    }

    // -- Insertion indicator helpers --

    private void ShowInsertionIndicator(ListView listView, int dropIndex)
    {
        ClearInsertionIndicator();

        // Show a colored top-border on the item at dropIndex, or bottom-border on the last item.
        int targetIndex = dropIndex < listView.Items.Count ? dropIndex : listView.Items.Count - 1;
        if (targetIndex < 0) return;

        if (listView.ContainerFromIndex(targetIndex) is ListViewItem container)
        {
            _lastIndicatorContainer = container;
            container.BorderBrush = AccentBrush;
            container.BorderThickness = dropIndex < listView.Items.Count
                ? new Thickness(0, 3, 0, 0)   // line above
                : new Thickness(0, 0, 0, 3);  // line below last item
        }
    }

    private void ClearInsertionIndicator()
    {
        if (_lastIndicatorContainer != null)
        {
            _lastIndicatorContainer.BorderBrush = null;
            _lastIndicatorContainer.BorderThickness = new Thickness(0);
            _lastIndicatorContainer = null;
        }
    }

    private async Task ShowItemDialog(LauncherItem? existingItem, ObservableCollection<LauncherItem>? targetList = null)
    {
        bool isEdit = existingItem != null;

        // Track state for this dialog session
        string fetchedIconPath = existingItem?.IconPath ?? "";
        bool isWebsite = existingItem?.IsWebsite ?? true;
        bool isPwa = existingItem?.IsPwa ?? false;
        bool openInAppWindow = existingItem?.OpenInAppWindow ?? false;
        string appWindowBrowser = existingItem?.AppWindowBrowser ?? "";
        string appWindowBrowserProfile = existingItem?.AppWindowBrowserProfile ?? "";

        // -- 1. Type selector --
        var typeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        typeCombo.Items.Add(new ComboBoxItem { Content = "Application" });
        typeCombo.Items.Add(new ComboBoxItem { Content = "Progressive Web App" });
        typeCombo.Items.Add(new ComboBoxItem { Content = "Website" });
        typeCombo.SelectedIndex = isEdit ? (existingItem!.IsPwa ? 1 : existingItem.IsWebsite ? 2 : 0) : 0;

        // -- 2. URL / Path --
        Microsoft.UI.Dispatching.DispatcherQueueTimer? debounceTimer = null;
        string lastFetchedPath = "";
        bool populating = false;

        var pathLabel = Label("URL");
        var pathBox = new TextBox
        {
            PlaceholderText = "https://example.com",
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // App-mode: ComboBox with installed apps + Browse
        var appPathCombo = new ComboBox
        {
            IsEditable = false,
            Margin = new Thickness(0, 0, 0, 0),
            DisplayMemberPath = "DisplayName",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        bool appCatalogLoaded = false;
        void EnsureAppCatalogLoaded()
        {
            if (appCatalogLoaded) return;
            appPathCombo.Items.Clear();
            foreach (var app in GetInstalledApplications())
                appPathCombo.Items.Add(app);
            appPathCombo.Items.Add(new InstalledApp("Browse\u2026", "__browse__"));
            appCatalogLoaded = true;
        }

        var browseButton = new Button
        {
            Content = "Browse",
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        var appPathRow = new Grid
        {
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 4
        };
        appPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        appPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(appPathCombo, 0);
        Grid.SetColumn(browseButton, 1);
        appPathRow.Children.Add(appPathCombo);
        appPathRow.Children.Add(browseButton);

        // PWA-mode: ComboBox with installed PWAs
        var pwaLabel = Label("Installed PWA");
        var pwaCombo = new ComboBox
        {
            IsEditable = false,
            Margin = new Thickness(0, 0, 0, 0),
            DisplayMemberPath = "DisplayName",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var noPwasHint = new TextBlock
        {
            Text = "No installed PWAs were detected in your browsers.",
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed
        };
        bool pwaCatalogLoaded = false;
        void EnsurePwaCatalogLoaded()
        {
            if (pwaCatalogLoaded) return;
            pwaCombo.Items.Clear();
            var pwas = GetInstalledPwas();
            foreach (var pwa in pwas)
                pwaCombo.Items.Add(pwa);
            noPwasHint.Visibility = pwas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            pwaCombo.Visibility = pwas.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            pwaCatalogLoaded = true;
        }

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
            PlaceholderText = "Auto-detected",
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

        // -- Type toggle logic --
        void UpdateTypeUI()
        {
            bool wasWebsite = isWebsite;
            bool wasPwa = isPwa;
            isWebsite = typeCombo.SelectedIndex == 2;
            isPwa = typeCombo.SelectedIndex == 1;
            if (!isWebsite && !isPwa)
            {
                // Registry + app scanning is expensive; only load on-demand.
                EnsureAppCatalogLoaded();
            }
            if (isPwa)
                EnsurePwaCatalogLoaded();

            pathLabel.Text = isWebsite ? "URL" : "Application";
            pathBox.PlaceholderText = isWebsite ? "https://example.com" : @"e.g. notepad.exe or C:\Program Files\...";
            pathLabel.Visibility = isPwa ? Visibility.Collapsed : Visibility.Visible;
            pathBox.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            appPathRow.Visibility = !isWebsite && !isPwa ? Visibility.Visible : Visibility.Collapsed;
            pwaLabel.Visibility = isPwa ? Visibility.Visible : Visibility.Collapsed;
            pwaCombo.Visibility = isPwa && pwaCombo.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            noPwasHint.Visibility = isPwa && pwaCombo.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            argsLabel.Visibility = !isWebsite && !isPwa ? Visibility.Visible : Visibility.Collapsed;
            argsBox.Visibility = !isWebsite && !isPwa ? Visibility.Visible : Visibility.Collapsed;
            appWindowToggle.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            refreshButton.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            UpdateAppWindowOptionsVisibility();

            bool typeChanged = wasWebsite != isWebsite || wasPwa != isPwa;
            if (typeChanged && !populating)
            {
                populating = true;
                pathBox.Text = "";
                appPathCombo.SelectedIndex = -1;
                pwaCombo.SelectedIndex = -1;
                argsBox.Text = "";
                nameBox.Text = "";
                fetchedIconPath = "";
                iconPreview.Source = null;
                iconStatus.Text = "Auto-detected";
                lastFetchedPath = "";
                appWindowToggle.IsOn = false;
                browserCombo.SelectedIndex = 0;
                appWindowBrowser = "";
                profileCombo.SelectedIndex = 0;
                appWindowBrowserProfile = "";
                populating = false;
            }
        }

        typeCombo.SelectionChanged += (s, ev) => UpdateTypeUI();

        // -- Auto-populate on path change (debounced) --
        async Task DoFetch(bool force)
        {
            var path = pathBox.Text.Trim();
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
                nameBox.PlaceholderText = "Auto-detected";

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
                    UpdateIconPreview(iconPreview, iconStatus, fetchedIconPath, true);
                }
                else
                {
                    iconStatus.Text = "Could not fetch icon";
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
                nameBox.PlaceholderText = "Auto-detected";
                if (!string.IsNullOrEmpty(appIcon))
                {
                    fetchedIconPath = appIcon;
                    UpdateIconPreview(iconPreview, iconStatus, fetchedIconPath, false);
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
                nameBox.PlaceholderText = "Auto-detected";
                if (!string.IsNullOrEmpty(appIcon))
                {
                    fetchedIconPath = appIcon;
                    UpdateIconPreview(iconPreview, iconStatus, fetchedIconPath, false);
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

        pathBox.TextChanged += (s, ev) => ScheduleFetch();
        refreshButton.Click += async (s, ev) =>
        {
            debounceTimer?.Stop();
            lastFetchedPath = "";
            await DoFetch(force: true);
        };

        // -- Wire up app-path combo events --
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
                ScheduleFetch();
            }
        }

        browseButton.Click += async (s, ev) => await BrowseForApp();
        appPathCombo.SelectionChanged += async (s, ev) =>
        {
            if (populating) return;
            if (appPathCombo.SelectedItem is InstalledApp app)
            {
                if (app.ExePath == "__browse__")
                {
                    appPathCombo.SelectedIndex = -1;
                    await BrowseForApp();
                    return;
                }
                populating = true;
                pathBox.Text = app.ExePath;
                if (string.IsNullOrEmpty(nameBox.Text))
                    nameBox.Text = app.DisplayName;
                populating = false;
                ScheduleFetch();
            }
        };

        // -- PWA combo selection --
        pwaCombo.SelectionChanged += async (s, ev) =>
        {
            if (populating) return;
            if (pwaCombo.SelectedItem is InstalledPwa pwa)
            {
                populating = true;
                pathBox.Text = pwa.Aumid;
                argsBox.Text = "";
                if (string.IsNullOrEmpty(nameBox.Text))
                    nameBox.Text = pwa.DisplayName;
                populating = false;

                // Extract icon from Windows shell registration (preferred), fall back to web favicon
                iconStatus.Text = "Fetching icon...";
                refreshButton.IsEnabled = false;
                var iconPath = FaviconService.GetPwaIconFromShell(pwa.Aumid)
                    ?? await FaviconService.FetchAndCacheAsync($"https://{pwa.Domain}/");
                refreshButton.IsEnabled = true;
                if (!string.IsNullOrEmpty(iconPath))
                {
                    fetchedIconPath = iconPath;
                    UpdateIconPreview(iconPreview, iconStatus, fetchedIconPath, true);
                }
                else
                {
                    iconStatus.Text = "No icon available";
                }
            }
        };

        // -- Populate for edit mode --
        if (isEdit)
        {
            populating = true;
            pathBox.Text = existingItem!.Path;
            if (existingItem.IsPwa)
            {
                EnsurePwaCatalogLoaded();
                // Try to match by AUMID stored in Path
                for (int i = 0; i < pwaCombo.Items.Count; i++)
                {
                    if (pwaCombo.Items[i] is InstalledPwa pwa &&
                        string.Equals(pwa.Aumid, existingItem.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        pwaCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            else if (!existingItem.IsWebsite)
            {
                EnsureAppCatalogLoaded();
                for (int i = 0; i < appPathCombo.Items.Count; i++)
                {
                    if (appPathCombo.Items[i] is InstalledApp ia &&
                        string.Equals(ia.ExePath, existingItem.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        appPathCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            argsBox.Text = existingItem.Arguments;
            nameBox.Text = existingItem.Name;
            UpdateIconPreview(iconPreview, iconStatus, fetchedIconPath, isWebsite);
            populating = false;
        }

        // -- Build form --
        var form = new StackPanel { MinWidth = 400 };
        form.Children.Add(Label("Type"));
        form.Children.Add(typeCombo);
        form.Children.Add(pwaLabel);
        form.Children.Add(pwaCombo);
        form.Children.Add(noPwasHint);
        form.Children.Add(pathLabel);
        form.Children.Add(pathBox);
        form.Children.Add(appPathRow);
        form.Children.Add(argsLabel);
        form.Children.Add(argsBox);
        form.Children.Add(appWindowToggle);
        form.Children.Add(appWindowOptionsPanel);
        form.Children.Add(Label("Name"));
        form.Children.Add(nameBox);
        form.Children.Add(Label("Icon"));
        form.Children.Add(iconRow);

        var validationHint = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 255, 69, 0)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        form.Children.Add(validationHint);

        UpdateTypeUI();

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = isEdit ? "Edit Item" : "Add Item",
            Content = form,
            PrimaryButtonText = isEdit ? "Save" : "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        void ValidateForm()
        {
            var missing = new List<string>();
            if (isPwa)
            {
                if (pwaCombo.SelectedIndex < 0)
                    missing.Add("PWA selection");
            }
            else if (string.IsNullOrWhiteSpace(pathBox.Text))
            {
                missing.Add(isWebsite ? "URL" : "Path");
            }
            if (string.IsNullOrWhiteSpace(nameBox.Text))
                missing.Add("Name");

            if (missing.Count > 0)
            {
                validationHint.Text = $"{string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} required.";
                validationHint.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = false;
            }
            else
            {
                validationHint.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
            }
        }

        pathBox.TextChanged += (s, ev) => ValidateForm();
        nameBox.TextChanged += (s, ev) => ValidateForm();
        pwaCombo.SelectionChanged += (s, ev) => ValidateForm();
        ValidateForm();

        var result = await dialog.ShowAsync();
        debounceTimer?.Stop();
        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        var finalPath = pathBox.Text.Trim();
        var args = argsBox.Text.Trim();
        var glyph = isWebsite || isPwa ? "\uE774" : "\uE8E5";

        if (isEdit)
        {
            existingItem!.Name = name;
            existingItem.Path = finalPath;
            existingItem.Arguments = args;
            existingItem.IconGlyph = glyph;
            existingItem.IconPath = fetchedIconPath;
            existingItem.IsWebsite = isWebsite;
            existingItem.IsPwa = isPwa;
            existingItem.OpenInAppWindow = isWebsite && openInAppWindow;
            existingItem.AppWindowBrowser = isWebsite && openInAppWindow ? appWindowBrowser : "";
            existingItem.AppWindowBrowserProfile = isWebsite && openInAppWindow ? appWindowBrowserProfile : "";
        }
        else
        {
            var newItem = new LauncherItem(name, finalPath, glyph, isWebsite, args, fetchedIconPath, isWebsite && openInAppWindow);
            newItem.IsPwa = isPwa;
            newItem.AppWindowBrowser = isWebsite && openInAppWindow ? appWindowBrowser : "";
            newItem.AppWindowBrowserProfile = isWebsite && openInAppWindow ? appWindowBrowserProfile : "";
            var target = targetList ?? SettingsManager.Current.LauncherItems;
            target.Add(newItem);
        }

        SaveAndUpdateTaskbar();
    }

    private static void UpdateIconPreview(Image preview, TextBlock status, string iconPath, bool isWebsite = true)
    {
        string iconLabel = isWebsite ? "favicon" : "icon";

        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            try
            {
                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = 32,
                    UriSource = new Uri(iconPath, UriKind.Absolute)
                };
                preview.Source = bitmap;
                status.Text = $"Auto {iconLabel}";
            }
            catch
            {
                preview.Source = null;
                status.Text = "Failed to load icon";
            }
        }
        else
        {
            preview.Source = null;
            status.Text = "No icon";
        }
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
    private static int GetDropIndex(ListView listView, DragEventArgs e)
    {
        var position = e.GetPosition(listView);
        for (int i = 0; i < listView.Items.Count; i++)
        {
            if (listView.ContainerFromIndex(i) is not ListViewItem container) continue;
            var transform = container.TransformToVisual(listView);
            var point = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
            if (position.Y < point.Y + container.ActualHeight / 2)
                return i;
        }
        return listView.Items.Count;
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
        if (sender is FrameworkElement fe && fe.Tag is LauncherItem item)
        {
            // If this is a shared group, remove its source config from settings
            if (item.IsGroup && !string.IsNullOrEmpty(item.SharedGroupId))
            {
                var toRemove = SettingsManager.Current.SharedGroupSources
                    .FirstOrDefault(s => s.Id == item.SharedGroupId);
                if (toRemove != null)
                    SettingsManager.Current.SharedGroupSources.Remove(toRemove);
            }

            var items = SettingsManager.Current.LauncherItems;
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
        var items = SettingsManager.Current.LauncherItems;
        if (items.Contains(item)) return items;
        foreach (var group in items.Where(i => i.IsGroup))
            if (group.Children.Contains(item)) return group.Children;
        return null;
    }

    private void MoveItemUp_Click(object sender, RoutedEventArgs e)
    {
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

                    foreach (dynamic item in folder.Items())
                    {
                        string? path = item.Path as string;
                        string? name = item.Name as string;
                        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) continue;
                        if (!path.Contains('!')) continue;

                        // Skip Chromium PWAs (handled by the PWA picker)
                        if (pwaPattern.IsMatch(path)) continue;

                        if (IsNonAppName(name)) continue;

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
        FlyoutWindow.InvalidateItems();
    }

    // ── Shared group handlers ────────────────────────────────────────────────

    /// <summary>
    /// Opens the "Share this group" dialog for a regular (unshared) group, allowing the
    /// owner to pick a local path or SFTP destination.
    /// </summary>
    private async void ShareGroupSetup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not LauncherItem group) return;
        await ShowShareGroupDialog(group);
    }

    /// <summary>
    /// Manually syncs a shared group — outgoing if owner, incoming if subscriber.
    /// Prompts for an SFTP password if no key is available.
    /// </summary>
    private async void SyncSharedGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button syncBtn || syncBtn.Tag is not LauncherItem group) return;

        var source = SettingsManager.Current.SharedGroupSources
            .FirstOrDefault(s => s.Id == group.SharedGroupId);
        if (source == null) return;

        string? password = null;
        if (source.SourceType == SharedGroupSourceType.Sftp && !SharedGroupSyncService.HasAutoKey(source))
            password = await PromptForSftpPasswordAsync($"Enter SSH password for {source.SftpHost}");

        // Swap button content to a spinner for the duration of the sync
        var originalContent = syncBtn.Content;
        syncBtn.IsEnabled = false;
        syncBtn.Content = new ProgressRing { IsActive = true, Width = 14, Height = 14 };

        (bool success, string message) result;
        try
        {
            if (source.IsOwner)
            {
                result = await SharedGroupSyncService.SyncOutgoingAsync(source, group, password);
            }
            else
            {
                result = await SharedGroupSyncService.SyncIncomingAsync(source, password);
                if (result.success)
                {
                    RefreshList();
                    SettingsManager.SaveSettings();
                    FlyoutWindow.InvalidateItems();
                }
            }
        }
        finally
        {
            syncBtn.Content = originalContent;
            syncBtn.IsEnabled = true;
        }

        if (!result.success)
        {
            await new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Sync Failed",
                Content = result.message,
                CloseButtonText = "OK"
            }.ShowAsync();
        }
    }

    /// <summary>
    /// Opens the "Add Shared Group" dialog so the user can subscribe to a group shared
    /// by another user via a local file path or SFTP connection.
    /// </summary>
    private async void ShowAddSharedGroupDialog_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddSharedGroupDialog();
    }

    // ── Share setup dialog ───────────────────────────────────────────────────

    private async Task ShowShareGroupDialog(LauncherItem group)
    {
        var source = new SharedGroupSource { IsOwner = true };

        var (form, getSource, getPassword, validateFunc) = BuildSharedGroupSourceForm(source, isSetup: true);
        var statusText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusRing = new ProgressRing { IsActive = false, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        statusRow.Children.Add(statusRing);
        statusRow.Children.Add(statusText);
        form.Children.Add(statusRow);

        var container = new StackPanel { MinWidth = 460 };
        container.Children.Add(new TextBlock
        {
            Text = $"Share \"{group.Name}\" so others can subscribe to it.",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            Opacity = 0.8
        });
        container.Children.Add(form);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Share Group",
            Content = new ScrollViewer { Content = container, MaxHeight = 500 },
            PrimaryButtonText = "Share",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };

        validateFunc(valid => dialog.IsPrimaryButtonEnabled = valid);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var finalSource = getSource();
        string? password = getPassword();

        // Wire up the group
        group.SharedGroupId = finalSource.Id;
        SettingsManager.Current.SharedGroupSources.Add(finalSource);
        SettingsManager.SaveSettings();

        // Do initial outgoing sync
        statusRow.Visibility = Visibility.Visible;
        statusRing.IsActive = true;
        statusText.Text = "Syncing…";
        var (success, message) = await SharedGroupSyncService.SyncOutgoingAsync(finalSource, group, password);
        statusRing.IsActive = false;

        if (!success)
        {
            // Rollback if initial sync fails
            group.SharedGroupId = string.Empty;
            SettingsManager.Current.SharedGroupSources.Remove(finalSource);
            SettingsManager.SaveSettings();

            await new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Sharing Failed",
                Content = $"Could not write the shared group file:\n{message}\n\nSharing was not set up.",
                CloseButtonText = "OK"
            }.ShowAsync();
        }
        else
        {
            RefreshList();
            SettingsManager.SaveSettings();
        }
    }

    // ── Subscribe dialog ─────────────────────────────────────────────────────

    private async Task ShowAddSharedGroupDialog()
    {
        var source = new SharedGroupSource { IsOwner = false };

        var nameBox = new TextBox
        {
            PlaceholderText = "e.g. Work Tools",
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var (sourceForm, getSource, getPassword, validateFunc) = BuildSharedGroupSourceForm(source, isSetup: false);

        var container = new StackPanel { MinWidth = 460 };
        container.Children.Add(new TextBlock
        {
            Text = "Subscribe to a group shared by another user. The group's content will be synced into your layout as read-only.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            Opacity = 0.8
        });
        container.Children.Add(Label("Group name (displayed in your launcher)"));
        container.Children.Add(nameBox);
        container.Children.Add(sourceForm);

        var validationHint = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 255, 69, 0)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        container.Children.Add(validationHint);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Add Shared Group",
            Content = new ScrollViewer { Content = container, MaxHeight = 520 },
            PrimaryButtonText = "Subscribe",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };

        bool sourceValid = false;
        validateFunc(valid =>
        {
            sourceValid = valid;
            dialog.IsPrimaryButtonEnabled = sourceValid && !string.IsNullOrWhiteSpace(nameBox.Text);
        });
        nameBox.TextChanged += (s, ev) =>
        {
            dialog.IsPrimaryButtonEnabled = sourceValid && !string.IsNullOrWhiteSpace(nameBox.Text);
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var finalSource = getSource();
        string? password = getPassword();
        string groupName = nameBox.Text.Trim();

        // Show a progress dialog while verifying and syncing (main dialog is already gone)
        var progressLabel = new TextBlock
        {
            Text = "Verifying\u2026",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        var progressRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        progressRow.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center });
        progressRow.Children.Add(progressLabel);
        var progressDialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Adding Shared Group",
            Content = progressRow
        };
        _ = progressDialog.ShowAsync();

        // Verify the source before committing
        var (verifyOk, itemCount, error) = await SharedGroupSyncService.VerifySourceAsync(finalSource, password);
        if (!verifyOk)
        {
            progressDialog.Hide();
            await new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Could Not Subscribe",
                Content = $"The shared group file could not be read:\n{error}",
                CloseButtonText = "OK"
            }.ShowAsync();
            return;
        }

        progressLabel.Text = "Syncing\u2026";

        // Add the source config FIRST so the template selector correctly identifies it
        // as a subscriber when the ListView renders the new item on LauncherItems.Add.
        SettingsManager.Current.SharedGroupSources.Add(finalSource);
        var newGroup = LauncherItem.CreateGroup(groupName);
        newGroup.SharedGroupId = finalSource.Id;
        SettingsManager.Current.LauncherItems.Add(newGroup);

        // Initial sync to populate children
        var (syncOk, syncMsg) = await SharedGroupSyncService.SyncIncomingAsync(finalSource, password);
        progressDialog.Hide();

        if (!syncOk)
        {
            // Rollback on sync failure
            SettingsManager.Current.LauncherItems.Remove(newGroup);
            SettingsManager.Current.SharedGroupSources.Remove(finalSource);
            await new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Sync Failed",
                Content = $"The group was found but could not be synced:\n{syncMsg}",
                CloseButtonText = "OK"
            }.ShowAsync();
            return;
        }

        SaveAndUpdateTaskbar();
        FlyoutWindow.InvalidateItems();
    }

    // ── Shared source form builder ────────────────────────────────────────────

    /// <summary>
    /// Builds a reusable form StackPanel that lets the user choose a local file path or
    /// SFTP connection for a shared group source.
    /// </summary>
    /// <returns>
    /// (form, getSource, getPassword, onValidChanged)
    ///   getSource()     – returns a <see cref="SharedGroupSource"/> with the entered values.
    ///   getPassword()   – returns the entered SFTP password (or null).
    ///   onValidChanged  – call with a callback; it will be invoked whenever the form validity changes.
    /// </returns>
    private (StackPanel form,
             Func<SharedGroupSource> getSource,
             Func<string?> getPassword,
             Action<Action<bool>> onValidChanged)
        BuildSharedGroupSourceForm(SharedGroupSource template, bool isSetup)
    {
        // ── Source type radio buttons ─────────────────────────────────────
        var localRadio = new RadioButton { Content = "Local file", GroupName = "SourceType", IsChecked = true };
        var sftpRadio = new RadioButton { Content = "SFTP server", GroupName = "SourceType" };
        var radioPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 0, 0, 12) };
        radioPanel.Children.Add(localRadio);
        radioPanel.Children.Add(sftpRadio);

        // ── Local path panel ──────────────────────────────────────────────
        var localPathBox = new TextBox
        {
            PlaceholderText = isSetup ? @"e.g. C:\Shared\my-group.xml" : @"e.g. C:\Shared\shared-group.xml",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 0)
        };
        var localBrowse = new Button
        {
            Content = "Browse…",
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        var localPathRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        localPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        localPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(localPathBox, 0);
        Grid.SetColumn(localBrowse, 1);
        localPathRow.Children.Add(localPathBox);
        localPathRow.Children.Add(localBrowse);

        var localPanel = new StackPanel { Spacing = 4 };
        localPanel.Children.Add(Label("File path"));
        localPanel.Children.Add(localPathRow);

        // ── SFTP panel ────────────────────────────────────────────────────
        var sftpHostBox = new TextBox { PlaceholderText = "hostname or IP", HorizontalAlignment = HorizontalAlignment.Stretch };
        var sftpPortBox = new NumberBox { Value = 22, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, Width = 90 };
        var sftpHostRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        sftpHostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sftpHostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sftpHostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sftpHostBox, 0);
        Grid.SetColumn(new TextBlock { Text = "Port", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) }, 1);
        Grid.SetColumn(sftpPortBox, 2);
        sftpHostRow.Children.Add(sftpHostBox);
        var portLabel = new TextBlock { Text = "Port", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
        Grid.SetColumn(portLabel, 1);
        sftpHostRow.Children.Add(portLabel);
        Grid.SetColumn(sftpPortBox, 2);
        sftpHostRow.Children.Add(sftpPortBox);

        var sftpUserBox = new TextBox { PlaceholderText = "username", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
        var sftpKeyBox = new TextBox { PlaceholderText = "(auto-detected)", HorizontalAlignment = HorizontalAlignment.Stretch };
        var sftpKeyBrowse = new Button { Content = "Browse…", Padding = new Thickness(10, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var sftpKeyRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        sftpKeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sftpKeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sftpKeyBox, 0);
        Grid.SetColumn(sftpKeyBrowse, 1);
        sftpKeyRow.Children.Add(sftpKeyBox);
        sftpKeyRow.Children.Add(sftpKeyBrowse);

        var sftpPathBox = new TextBox
        {
            PlaceholderText = "~/.config/LittleLauncher/my-group.xml",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // Password (shown only when SFTP active)
        var sftpPasswordBox = new PasswordBox { PlaceholderText = "SSH password (if no key)", HorizontalAlignment = HorizontalAlignment.Stretch };

        var sftpPanel = new StackPanel { Spacing = 0 };
        sftpPanel.Children.Add(Label("Host"));
        sftpPanel.Children.Add(sftpHostRow);
        sftpPanel.Children.Add(Label("Username"));
        sftpPanel.Children.Add(sftpUserBox);
        sftpPanel.Children.Add(Label("Private key path"));
        sftpPanel.Children.Add(sftpKeyRow);
        sftpPanel.Children.Add(Label("Remote file path"));
        sftpPanel.Children.Add(sftpPathBox);
        sftpPanel.Children.Add(Label("Password (if no key)"));
        sftpPanel.Children.Add(sftpPasswordBox);

        // ── Wire file picker for local path ───────────────────────────────
        localBrowse.Click += async (s, ev) =>
        {
            if (isSetup)
            {
                var picker = new FileSavePicker();
                InitializePicker(picker);
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.SuggestedFileName = "shared-group";
                picker.FileTypeChoices.Add("XML files", new List<string> { ".xml" });
                var file = await picker.PickSaveFileAsync();
                if (file != null) localPathBox.Text = file.Path;
            }
            else
            {
                var picker = new FileOpenPicker();
                InitializePicker(picker);
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".xml");
                picker.FileTypeFilter.Add("*");
                var file = await picker.PickSingleFileAsync();
                if (file != null) localPathBox.Text = file.Path;
            }
        };

        // ── Wire key file picker ──────────────────────────────────────────
        sftpKeyBrowse.Click += async (s, ev) =>
        {
            var picker = new FileOpenPicker();
            InitializePicker(picker);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file != null) sftpKeyBox.Text = file.Path;
        };

        // ── Panel visibility sync ─────────────────────────────────────────
        sftpPanel.Visibility = Visibility.Collapsed;
        Action? notifyValidChanged = null;

        void UpdatePanelVisibility()
        {
            bool sftp = sftpRadio.IsChecked == true;
            localPanel.Visibility = sftp ? Visibility.Collapsed : Visibility.Visible;
            sftpPanel.Visibility = sftp ? Visibility.Visible : Visibility.Collapsed;
            notifyValidChanged?.Invoke();
        }

        localRadio.Checked += (s, ev) => UpdatePanelVisibility();
        sftpRadio.Checked += (s, ev) => UpdatePanelVisibility();
        localPathBox.TextChanged += (s, ev) => notifyValidChanged?.Invoke();
        sftpHostBox.TextChanged += (s, ev) => notifyValidChanged?.Invoke();
        sftpPathBox.TextChanged += (s, ev) => notifyValidChanged?.Invoke();

        // ── Assemble form ─────────────────────────────────────────────────
        var form = new StackPanel { Spacing = 4 };
        form.Children.Add(Label("Destination"));
        form.Children.Add(radioPanel);
        form.Children.Add(localPanel);
        form.Children.Add(sftpPanel);

        // ── Getters ───────────────────────────────────────────────────────
        Func<SharedGroupSource> getSource = () =>
        {
            bool sftp = sftpRadio.IsChecked == true;
            return new SharedGroupSource
            {
                Id = template.Id,
                IsOwner = template.IsOwner,
                SourceType = sftp ? SharedGroupSourceType.Sftp : SharedGroupSourceType.LocalPath,
                LocalPath = sftp ? "" : localPathBox.Text.Trim(),
                SftpHost = sftp ? sftpHostBox.Text.Trim() : "",
                SftpPort = sftp && !double.IsNaN(sftpPortBox.Value) ? (int)sftpPortBox.Value : 22,
                SftpUsername = sftp ? sftpUserBox.Text.Trim() : "",
                SftpPrivateKeyPath = sftp ? sftpKeyBox.Text.Trim() : "",
                SftpRemotePath = sftp ? sftpPathBox.Text.Trim() : ""
            };
        };

        Func<string?> getPassword = () =>
        {
            if (sftpRadio.IsChecked != true) return null;
            var pw = sftpPasswordBox.Password;
            return string.IsNullOrEmpty(pw) ? null : pw;
        };

        Action<Action<bool>> onValidChanged = callback =>
        {
            notifyValidChanged = () =>
            {
                bool sftp = sftpRadio.IsChecked == true;
                bool valid = sftp
                    ? !string.IsNullOrWhiteSpace(sftpHostBox.Text) && !string.IsNullOrWhiteSpace(sftpPathBox.Text)
                    : !string.IsNullOrWhiteSpace(localPathBox.Text);
                callback(valid);
            };
            notifyValidChanged();
        };

        return (form, getSource, getPassword, onValidChanged);
    }

    // ── SFTP password prompt ──────────────────────────────────────────────────

    private async Task<string?> PromptForSftpPasswordAsync(string prompt)
    {
        var passwordBox = new PasswordBox
        {
            PlaceholderText = "SSH password",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var form = new StackPanel { MinWidth = 340, Spacing = 8 };
        form.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "SSH Password",
            Content = form,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        var pw = passwordBox.Password;
        return string.IsNullOrEmpty(pw) ? null : pw;
    }

    private async void ExportItems_Click(object sender, RoutedEventArgs e)
    {
        var items = SettingsManager.Current.LauncherItems;
        if (items.Count == 0) return;

        var picker = new FileSavePicker();
        InitializePicker(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "launcher-items";
        picker.FileTypeChoices.Add("XML files", [".xml"]);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var list = new List<LauncherItem>(items);
            var serializer = new XmlSerializer(typeof(List<LauncherItem>));
            using var stream = new FileStream(file.Path, FileMode.Create, FileAccess.Write);
            serializer.Serialize(stream, list);
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
        picker.FileTypeFilter.Add(".xml");

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        List<LauncherItem>? imported;
        try
        {
            var serializer = new XmlSerializer(typeof(List<LauncherItem>));
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read);
            imported = serializer.Deserialize(stream) as List<LauncherItem>;
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

        var collection = SettingsManager.Current.LauncherItems;

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
}