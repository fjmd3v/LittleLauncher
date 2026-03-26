// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Services;
using LittleLauncher.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Pages;

public sealed partial class LaunchersPage : Page
{
    public LaunchersPage()
    {
        InitializeComponent();
        RebuildLauncherCards();
    }

    // ── Build the UI dynamically (one card per launcher) ──────────────

    private void RebuildLauncherCards()
    {
        // Remove all children except the Add button (last child)
        while (LaunchersPanel.Children.Count > 1)
            LaunchersPanel.Children.RemoveAt(0);

        var launchers = SettingsManager.Current.Launchers;
        int insertIndex = 0;

        foreach (var launcher in launchers)
        {
            var card = BuildLauncherCard(launcher);
            LaunchersPanel.Children.Insert(insertIndex++, card);
        }
    }

    private static int CountLauncherItems(IEnumerable<LauncherItem> items)
    {
        int count = 0;
        foreach (var item in items)
        {
            if (item.IsColumnBreak) continue;
            if (item.IsGroup)
                count += CountLauncherItems(item.Children);
            else
                count++;
        }
        return count;
    }

    private Border BuildLauncherCard(Launcher launcher)
    {
        // ── Name row ────────────────────────────────────────────────
        var nameBox = new TextBox
        {
            PlaceholderText = "Launcher name",
            Text = launcher.Name,
            MinWidth = 160,
            MaxWidth = 280,
        };
        nameBox.TextChanged += (s, e) =>
        {
            launcher.Name = nameBox.Text;
        };
        void commitName()
        {
            launcher.Name = nameBox.Text;
            SettingsManager.SaveSettings();
            // Update the card header title to reflect the new name
            if (nameBox.Tag is TextBlock title)
                title.Text = launcher.Name;
        }
        nameBox.KeyDown += (s, e) =>
        {
            if (e.Key == global::Windows.System.VirtualKey.Enter)
            {
                commitName();
                e.Handled = true;
            }
        };
        nameBox.LostFocus += (s, e) => commitName();

        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nameLabel.Children.Add(new TextBlock { Text = "Name", FontSize = 14 });
        nameLabel.Children.Add(new TextBlock { Text = "Display name in tray icon tooltip", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(nameLabel, 0);
        Grid.SetColumn(nameBox, 1);
        nameRow.Children.Add(nameLabel);
        nameRow.Children.Add(nameBox);

        // ── Icon mode combo ─────────────────────────────────────────
        var iconCombo = BuildIconModeCombo(launcher);

        var iconRow = new Grid();
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        iconLabel.Children.Add(new TextBlock { Text = "Icon", FontSize = 14 });
        iconLabel.Children.Add(new TextBlock { Text = "Icon style for this launcher", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(iconLabel, 0);
        Grid.SetColumn(iconCombo, 1);
        iconRow.Children.Add(iconLabel);
        iconRow.Children.Add(iconCombo);

        // ── Custom icon path row (visible only when TrayIconMode == 12) ────
        var customIconRow = BuildCustomIconRow(launcher);
        customIconRow.Visibility = launcher.TrayIconMode == 12 ? Visibility.Visible : Visibility.Collapsed;
        iconCombo.SelectionChanged += (s, e) =>
        {
            customIconRow.Visibility = iconCombo.SelectedIndex == 12
                ? Visibility.Visible : Visibility.Collapsed;
        };

        // ── Show tray icon toggle ────────────────────────────────────
        var showToggle = new ToggleSwitch
        {
            IsOn = !launcher.NIconHide,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };
        showToggle.Toggled += (s, e) =>
        {
            launcher.NIconHide = !showToggle.IsOn;
            SettingsManager.SaveSettings();
        };

        var hideRow = new Grid();
        hideRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hideRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hideLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hideLabel.Children.Add(new TextBlock { Text = "Show in Tray", FontSize = 14 });
        hideLabel.Children.Add(new TextBlock { Text = "Show this launcher's icon in the system tray", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(hideLabel, 0);
        Grid.SetColumn(showToggle, 1);
        hideRow.Children.Add(hideLabel);
        hideRow.Children.Add(showToggle);

        // ── Taskbar icon row (with Pin button inline) ───────────────
        var pinBtn = new Button
        {
            Content = "Pin to Taskbar",
            Tag = launcher,
        };
        pinBtn.Click += PinToTaskbar_Click;

        var taskbarRow = new Grid();
        taskbarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        taskbarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var taskbarLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        taskbarLabel.Children.Add(new TextBlock { Text = "Show in Taskbar", FontSize = 14 });
        taskbarLabel.Children.Add(new TextBlock { Text = "Pin a shortcut to the taskbar for quick access", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(taskbarLabel, 0);
        Grid.SetColumn(pinBtn, 1);
        taskbarRow.Children.Add(taskbarLabel);
        taskbarRow.Children.Add(pinBtn);

        // ── Items row (clickable drill-in with chevron) ─────────────
        int itemCount = CountLauncherItems(launcher.Items);

        var itemsLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        itemsLabel.Children.Add(new TextBlock { Text = "Items", FontSize = 14 });
        itemsLabel.Children.Add(new TextBlock { Text = $"{itemCount} item{(itemCount == 1 ? "" : "s")}", FontSize = 12, Opacity = 0.5 });

        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var itemsRowInner = new Grid();
        itemsRowInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        itemsRowInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(itemsLabel, 0);
        Grid.SetColumn(chevron, 1);
        itemsRowInner.Children.Add(itemsLabel);
        itemsRowInner.Children.Add(chevron);

        var itemsRow = new Button
        {
            Content = itemsRowInner,
            Tag = launcher,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };
        itemsRow.Click += EditItems_Click;

        // ── Delete button (in header bar) ──────────────────────────
        var deleteBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            Tag = launcher,
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };
        deleteBtn.Click += DeleteLauncher_Click;

        // ── View mode combo ──────────────────────────────────────────
        var viewModeCombo = new ComboBox { MinWidth = 160 };
        viewModeCombo.Items.Add("Icons");
        viewModeCombo.Items.Add("List");
        viewModeCombo.SelectedIndex = Math.Clamp(launcher.ViewMode, 0, 1);
        viewModeCombo.SelectionChanged += (s, e) =>
        {
            launcher.ViewMode = viewModeCombo.SelectedIndex;
            SettingsManager.SaveSettings();
            FlyoutWindow.InvalidateItems(launcher.Id);
        };

        var viewModeRow = new Grid();
        viewModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        viewModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var viewModeLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        viewModeLabel.Children.Add(new TextBlock { Text = "View Mode", FontSize = 14 });
        viewModeLabel.Children.Add(new TextBlock { Text = "How items appear in the flyout popup", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(viewModeLabel, 0);
        Grid.SetColumn(viewModeCombo, 1);
        viewModeRow.Children.Add(viewModeLabel);
        viewModeRow.Children.Add(viewModeCombo);

        // ── Card container ──────────────────────────────────────────
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(nameRow);
        content.Children.Add(iconRow);
        content.Children.Add(customIconRow);
        content.Children.Add(viewModeRow);
        content.Children.Add(hideRow);
        content.Children.Add(taskbarRow);
        content.Children.Add(itemsRow);

        var headerIcon = new FontIcon { Glyph = "\uF0E2", FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var headerTitle = new TextBlock { Text = launcher.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        nameBox.Tag = headerTitle; // so commitName() can update the header

        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerLeft.Children.Add(headerIcon);
        headerLeft.Children.Add(headerTitle);

        // ── Shared badge ────────────────────────────────────────────
        if (launcher.IsShared)
        {
            string badgeText = launcher.SharedTwoWay
                ? "Shared"
                : (launcher.IsSharedOwner ? "Shared (owner)" : "Subscribed");

            bool isAccent = launcher.SharedTwoWay || launcher.IsSharedOwner;
            var badge = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (isAccent)
                badge.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            else
            {
                badge.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
                badge.BorderThickness = new Thickness(1);
            }

            var badgeFg = isAccent
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            var badgeStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            badgeStack.Children.Add(new FontIcon { Glyph = "\uE72D", FontSize = 10, Foreground = badgeFg });
            badgeStack.Children.Add(new TextBlock { Text = badgeText, FontSize = 11, Foreground = badgeFg, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            badge.Child = badgeStack;
            headerLeft.Children.Add(badge);
        }

        // ── Header buttons (sync, settings, share, delete) ─────────
        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

        if (launcher.IsShared)
        {
            var syncBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE895", FontSize = 12 },
                Tag = launcher,
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(syncBtn, "Sync now");
            syncBtn.Click += SyncSharedLauncher_Click;
            headerButtons.Children.Add(syncBtn);

            var settingsBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE713", FontSize = 12 },
                Tag = launcher,
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(settingsBtn, "Sharing settings");
            settingsBtn.Click += ShareLauncher_Click;
            headerButtons.Children.Add(settingsBtn);
        }

        if (!launcher.IsShared)
        {
            var shareBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE72D", FontSize = 12 },
                Tag = launcher,
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(shareBtn, "Share this launcher");
            shareBtn.Click += ShareLauncher_Click;
            headerButtons.Children.Add(shareBtn);
        }

        headerButtons.Children.Add(deleteBtn);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(headerLeft, 0);
        Grid.SetColumn(headerButtons, 1);
        header.Children.Add(headerLeft);
        header.Children.Add(headerButtons);

        var innerStack = new StackPanel { Spacing = 8 };
        innerStack.Children.Add(header);
        innerStack.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 0, 0, 0),
        });
        innerStack.Children.Add(content);

        var card = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8),
            Child = innerStack,
            Tag = launcher,
        };

        return card;
    }

    private static ComboBox BuildIconModeCombo(Launcher launcher)
    {
        var combo = new ComboBox { MinWidth = 160 };

        // Colour presets
        string[] colorNames = ["Blue", "Green", "Teal", "Red", "Orange", "Purple"];
        string iconsDir = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons");
        foreach (var name in colorNames)
        {
            double sz = 20;
            var preview = new Image { Width = sz, Height = sz };
            string pngPath = Path.Combine(iconsDir, $"{name}.png");
            if (File.Exists(pngPath))
                preview.Source = new BitmapImage(new Uri(pngPath));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(preview);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            combo.Items.Add(new ComboBoxItem { Content = row });
        }

        // Glyph presets
        (string glyph, string label)[] glyphs = [
            ("\uE840", "Pin"), ("\uE734", "Star"), ("\uEB51", "Heart"),
            ("\uE945", "Lightning"), ("\uE721", "Search"), ("\uE774", "Globe"),
        ];
        foreach (var (g, label) in glyphs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FontIcon { Glyph = g, FontSize = 16 });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            combo.Items.Add(new ComboBoxItem { Content = row });
        }

        combo.Items.Add(new ComboBoxItem { Content = "Custom..." });
        combo.SelectedIndex = Math.Clamp(launcher.TrayIconMode, 0, combo.Items.Count - 1);

        combo.SelectionChanged += (s, e) =>
        {
            launcher.TrayIconMode = combo.SelectedIndex;
            SettingsManager.SaveSettings();
        };

        return combo;
    }

    private Grid BuildCustomIconRow(Launcher launcher)
    {
        var pathText = new TextBlock
        {
            Text = string.IsNullOrEmpty(launcher.CustomTrayIconPath)
                ? "No file selected"
                : System.IO.Path.GetFileName(launcher.CustomTrayIconPath),
            FontSize = 12,
            Opacity = 0.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
        };

        var browseBtn = new Button { Content = "Browse..." };
        browseBtn.Click += async (s, e) =>
        {
            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".ico");
            picker.FileTypeFilter.Add(".png");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(SettingsWindow.GetCurrent()!));
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                string destPath = Path.Combine(MainWindow.GetPhysicalAppDataDir(), $"custom-tray-icon-{launcher.Id}{Path.GetExtension(file.Path)}");
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file.Path, destPath, overwrite: true);
                launcher.CustomTrayIconPath = destPath;
                pathText.Text = Path.GetFileName(destPath);
                SettingsManager.SaveSettings();
            }
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = "Custom Icon", FontSize = 14 });
        label.Children.Add(pathText);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(browseBtn, 1);
        row.Children.Add(label);
        row.Children.Add(browseBtn);
        return row;
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void AddLauncherButton_Click(object sender, RoutedEventArgs e)
    {
        var newLauncher = new Launcher
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"Launcher {SettingsManager.Current.Launchers.Count + 1}",
        };
        SettingsManager.Current.Launchers.Add(newLauncher);
        SettingsManager.SaveSettings();

        // Tell MainWindow to create a tray icon for the new launcher
        MainWindow.Current?.RefreshTrayIcons();

        RebuildLauncherCards();
    }

    private void EditItems_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Launcher launcher)
        {
            LauncherItemsPage.TargetLauncher = launcher;
            SettingsWindow.GetCurrent()?.NavigateToLauncherItems(launcher);
        }
    }

    private async void PinToTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;

        // Ensure the per-launcher Start Menu shortcut exists with the correct
        // AUMID, target args, and icon.  When the user pins the running
        // companion exe, Windows matches this shortcut and uses its properties.
        MainWindow.EnsureFlyoutStartMenuShortcuts();

        // Launch the companion exe in --pin mode with the launcher's ID
        string flyoutExe = Path.Combine(MainWindow.GetPhysicalAppDataDir(), "LittleLauncherFlyout.exe");
        if (!File.Exists(flyoutExe))
            flyoutExe = Path.Combine(AppContext.BaseDirectory, "LittleLauncherFlyout.exe");

        if (!File.Exists(flyoutExe))
        {
            await ShowErrorDialog("The companion flyout exe was not found. Build the project first.");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = flyoutExe,
            Arguments = $"--pin --launcher {launcher.Id}",
            UseShellExecute = true,
        });
    }

    private async void DeleteLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;

        if (SettingsManager.Current.Launchers.Count <= 1)
        {
            await ShowErrorDialog("You must keep at least one launcher.");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Delete Launcher",
            Content = $"Delete the launcher \"{launcher.Name}\" and all its items? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        // Dispose the flyout window for this launcher
        FlyoutWindow.DisposeLauncher(launcher.Id);

        SettingsManager.Current.Launchers.Remove(launcher);
        SettingsManager.SaveSettings();

        // Tell MainWindow to remove the tray icon
        MainWindow.Current?.RefreshTrayIcons();

        RebuildLauncherCards();
    }

    // ── Shared launcher handlers ───────────────────────────────────

    private async void AddSharedLauncherButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddSharedLauncherDialog();
    }

    private async void ShareLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Launcher launcher)
            await ShowShareLauncherDialog(launcher);
    }

    private async void SyncSharedLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Launcher launcher) return;

        string? password = null;
        if (!SftpSyncService.HasAutoKeyForShared(launcher))
        {
            password = await ShowPasswordPrompt();
            if (password == null) return; // user cancelled
        }

        bool canPush = launcher.SharedTwoWay || launcher.IsSharedOwner;
        bool canPull = launcher.SharedTwoWay || !launcher.IsSharedOwner;
        bool ok = true;
        string msg = "";

        if (canPush)
        {
            (ok, msg) = await SftpSyncService.ShareLauncherAsync(launcher, password);
        }
        if (ok && canPull)
        {
            (ok, msg) = await SftpSyncService.SyncSharedLauncherAsync(launcher, password);
        }

        if (!ok)
        {
            await ShowErrorDialog(msg);
            return;
        }

        // Refresh flyouts after sync
        FlyoutWindow.InvalidateAllItems();

        RebuildLauncherCards();
    }

    private async Task ShowShareLauncherDialog(Launcher launcher)
    {
        var (formPanel, modeCombo, pathBox, hostBox, portBox, userBox, keyBox, directionCombo) = BuildShareForm(launcher);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = launcher.IsShared
                ? "Sharing Settings"
                : "Share Launcher",
            Content = new ScrollViewer { Content = formPanel, MaxHeight = 400 },
            PrimaryButtonText = launcher.IsShared ? "Update" : "Share",
            SecondaryButtonText = launcher.IsShared
                ? "Stop Sharing"
                : null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            // Stop sharing
            launcher.IsShared = false;
            launcher.IsSharedOwner = false;
            launcher.SharedTwoWay = false;
            launcher.SharedSyncMode = 0;
            launcher.SharedPath = "";
            launcher.SharedSftpHost = "";
            launcher.SharedSftpPort = 22;
            launcher.SharedSftpUsername = "";
            launcher.SharedSftpPrivateKeyPath = "";
            SettingsManager.SaveSettings();
            RebuildLauncherCards();
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        int mode = modeCombo.SelectedIndex; // 0 = File, 1 = SFTP
        string path = pathBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(path))
        {
            await ShowErrorDialog("Path is required.");
            return;
        }

        if (mode == 1 && string.IsNullOrWhiteSpace(hostBox.Text))
        {
            await ShowErrorDialog("SFTP host is required.");
            return;
        }

        launcher.SharedSyncMode = mode;
        launcher.SharedPath = path;
        launcher.SharedSftpHost = hostBox.Text.Trim();
        launcher.SharedSftpPort = int.TryParse(portBox.Text, out int p) ? p : 22;
        launcher.SharedSftpUsername = userBox.Text.Trim();
        launcher.SharedSftpPrivateKeyPath = keyBox.Text.Trim();

        bool isTwoWay = directionCombo.SelectedIndex == 0;
        launcher.SharedTwoWay = isTwoWay;
        launcher.IsShared = true;
        if (!isTwoWay)
            launcher.IsSharedOwner = true;
        SettingsManager.SaveSettings();

        // Initial push
        string? password = null;
        if (!SftpSyncService.HasAutoKeyForShared(launcher))
        {
            password = await ShowPasswordPrompt();
            if (password == null) { RebuildLauncherCards(); return; }
        }

        var (ok, msg) = await SftpSyncService.ShareLauncherAsync(launcher, password);
        if (!ok)
            await ShowErrorDialog(msg);

        RebuildLauncherCards();
    }

    private async Task ShowAddSharedLauncherDialog()
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "Launcher name",
            Text = "Shared Launcher",
            Margin = new Thickness(0, 0, 0, 12),
        };

        var tempLauncher = new Launcher();
        var (formPanel, modeCombo, pathBox, hostBox, portBox, userBox, keyBox, directionCombo) = BuildShareForm(tempLauncher);

        var fullPanel = new StackPanel { Spacing = 4 };
        fullPanel.Children.Add(new TextBlock { Text = "Name", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        fullPanel.Children.Add(nameBox);
        fullPanel.Children.Add(formPanel);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Add Shared Launcher",
            Content = new ScrollViewer { Content = fullPanel, MaxHeight = 400 },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        int mode = modeCombo.SelectedIndex; // 0 = File, 1 = SFTP
        string path = pathBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(path))
        {
            await ShowErrorDialog("Path is required.");
            return;
        }

        if (mode == 1 && string.IsNullOrWhiteSpace(hostBox.Text))
        {
            await ShowErrorDialog("SFTP host is required.");
            return;
        }

        bool isTwoWay = directionCombo.SelectedIndex == 0;
        var newLauncher = new Launcher
        {
            Id = Guid.NewGuid().ToString(),
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "Shared Launcher" : nameBox.Text.Trim(),
            IsShared = true,
            IsSharedOwner = false,
            SharedTwoWay = isTwoWay,
            SharedSyncMode = mode,
            SharedPath = path,
            SharedSftpHost = hostBox.Text.Trim(),
            SharedSftpPort = int.TryParse(portBox.Text, out int p) ? p : 22,
            SharedSftpUsername = userBox.Text.Trim(),
            SharedSftpPrivateKeyPath = keyBox.Text.Trim(),
        };

        // Verify remote before adding
        string? password = null;
        if (!SftpSyncService.HasAutoKeyForShared(newLauncher))
        {
            password = await ShowPasswordPrompt();
            if (password == null) return;
        }

        var (verified, itemCount, error) = await SftpSyncService.VerifySharedLauncherAsync(newLauncher, password);
        if (!verified)
        {
            await ShowErrorDialog($"Could not verify: {error}");
            return;
        }

        SettingsManager.Current.Launchers.Add(newLauncher);
        SettingsManager.SaveSettings();

        // Initial pull
        var (ok, msg) = await SftpSyncService.SyncSharedLauncherAsync(newLauncher, password);
        if (!ok)
            await ShowErrorDialog(msg);

        MainWindow.Current?.RefreshTrayIcons();
        RebuildLauncherCards();
    }

    // ── Shared dialog helpers ───────────────────────────────────────

    private static (StackPanel Panel, ComboBox ModeCombo, TextBox PathBox,
        TextBox HostBox, TextBox PortBox, TextBox UserBox, TextBox KeyBox,
        ComboBox DirectionCombo)
        BuildShareForm(Launcher launcher)
    {
        // ── Direction ───────────────────────────────────────────────
        var directionCombo = new ComboBox { MinWidth = 160 };
        directionCombo.Items.Add("2-way (all participants can edit)");
        directionCombo.Items.Add("1-way (owner publishes, others subscribe)");
        directionCombo.SelectedIndex = launcher.SharedTwoWay ? 0 : 1;

        var modeCombo = new ComboBox { MinWidth = 160 };
        modeCombo.Items.Add("File (local or network)");
        modeCombo.Items.Add("SFTP");
        modeCombo.SelectedIndex = launcher.SharedSyncMode;

        var pathBox = new TextBox
        {
            PlaceholderText = launcher.IsFileSync ? @"C:\shared\launcher.json or \\server\share\launcher.json" : "~/shared/launcher.json",
            Text = launcher.SharedPath,
        };

        var hostBox = new TextBox { PlaceholderText = "hostname or IP", Text = launcher.SharedSftpHost };
        var portBox = new TextBox { PlaceholderText = "22", Text = launcher.SharedSftpPort == 22 ? "" : launcher.SharedSftpPort.ToString() };
        var userBox = new TextBox { PlaceholderText = Environment.UserName, Text = launcher.SharedSftpUsername };
        var keyBox = new TextBox { PlaceholderText = "auto-detect (~/.ssh/)", Text = launcher.SharedSftpPrivateKeyPath };

        // SFTP-specific fields panel
        var sftpPanel = new StackPanel { Spacing = 4 };

        void AddField(StackPanel target, string label, UIElement element)
        {
            target.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
            target.Children.Add(element);
        }

        AddField(sftpPanel, "SFTP Host", hostBox);
        AddField(sftpPanel, "Port", portBox);
        AddField(sftpPanel, "Username", userBox);
        AddField(sftpPanel, "Private Key", keyBox);

        sftpPanel.Visibility = launcher.IsSftpSync ? Visibility.Visible : Visibility.Collapsed;

        // Update visibility and placeholder when mode changes
        modeCombo.SelectionChanged += (s, e) =>
        {
            bool isSftp = modeCombo.SelectedIndex == 1;
            sftpPanel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
            pathBox.PlaceholderText = isSftp
                ? "~/shared/launcher.json"
                : @"C:\shared\launcher.json or \\server\share\launcher.json";
        };

        var panel = new StackPanel { Spacing = 4 };
        AddField(panel, "Direction", directionCombo);
        AddField(panel, "Mode", modeCombo);
        AddField(panel, "Path", pathBox);
        panel.Children.Add(sftpPanel);

        return (panel, modeCombo, pathBox, hostBox, portBox, userBox, keyBox, directionCombo);
    }

    private async Task<string?> ShowPasswordPrompt()
    {
        var passwordBox = new PasswordBox { PlaceholderText = "SSH key passphrase or password" };

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Authentication Required",
            Content = passwordBox,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        return passwordBox.Password;
    }

    private async Task ShowErrorDialog(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}
