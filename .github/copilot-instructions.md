# Little Launcher — Copilot Instructions

## Project overview

Little Launcher is a .NET 10 WinUI 3 desktop application (unpackaged) that provides a system-tray launcher with a flyout popup for shortcuts. It also syncs settings to a remote server via SSH/SFTP.

## Architecture

- **Single-instance app** enforced via a named `Mutex` ("LittleLauncher"). A second launch signals the first instance via `PostMessage` with registered window messages (`LittleLauncher_ShowFlyout_{launcherId}`, `LittleLauncher_ShowSettings`).
- **Launchers** — users define one or more named `Launcher` objects (model: `LittleLauncher.Models.Launcher`). Each launcher has its own `Items` collection, tray icon (mode + optional custom path), and `NIconHide` visibility flag. Settings include a `Launchers: ObservableCollection<Launcher>`. On first run with old settings, the legacy `LauncherItems` list is migrated into a "Default" launcher.
- **MainWindow** is invisible (moved off-screen, 1×1). It owns one native tray icon per launcher via `Shell_NotifyIcon` (stored in `_trayIcons: Dictionary<string, TrayIconEntry>`). Uses `WS_EX_TOOLWINDOW` to hide from Alt-Tab.
- **FlyoutWindow** is a popup that displays launcher items with icons. One instance is maintained per launcher (`_instances: Dictionary<string, FlyoutWindow>`). Shown from a launcher's tray icon click, positioned above the taskbar, dismissed on focus loss or Escape.
- **SettingsWindow** is a WinUI 3 window with `MicaBackdrop`. It uses `NavigationView` with page-based navigation (Home, Launchers, Cloud Sync, Settings, About). The Launchers page has a drill-in to LauncherItemsPage via `SettingsWindow.NavigateToLauncherItems(Launcher)`.
- **Settings** are serialised to `%AppData%\LittleLauncher\settings.json` via `System.Text.Json`, managed by the fully static `SettingsManager`. On first load, migrates from legacy `settings.xml` (XmlSerializer) to JSON.
- **SftpSyncService** uses SSH.NET for async upload/download of all launchers (as `launchers.json`) to a configurable remote server. Also handles per-launcher shared sync (owner pushes, subscriber pulls via separate SFTP connections).
- **LauncherShortcut** (`LittleLauncherFlyout.exe`) is a companion exe pinned to the taskbar. It signals the main app via `PostMessage` with a launcher-specific message (`LittleLauncher_ShowFlyout_{launcherId}`) then exits. Accepts `--launcher {guid}` argument (also supports legacy `--layout` for backward compat). Release builds are Native AOT for instant startup — see the performance warning in `ARCHITECTURE.md`.

## Key namespaces

| Namespace | Contents |
|---|---|
| `LittleLauncher` | App, MainWindow, SettingsWindow |
| `LittleLauncher.Classes` | NativeMethods, ThemeManager |
| `LittleLauncher.Classes.Settings` | SettingsManager |
| `LittleLauncher.Models` | LauncherItem, Launcher, SshConnectionProfile |
| `LittleLauncher.Pages` | All settings pages |
| `LittleLauncher.Services` | SftpSyncService, AutoSyncService, FaviconService, UpdateService |
| `LittleLauncher.ViewModels` | UserSettings |
| `LittleLauncher.Windows` | FlyoutWindow |

**Note:** The `LittleLauncher.Windows` namespace shadows the WinRT `Windows.*` namespace. Use `global::Windows.` prefix when accessing WinRT types (e.g. `global::Windows.Graphics.PointInt32`).

**Note:** `LittleLauncher.Models.Launcher` conflicts with other framework types. Use `using Launcher = LittleLauncher.Models.Launcher;` at the top of any file where disambiguation is needed.

## Conventions

- Use `[ObservableProperty]` from CommunityToolkit.Mvvm for all bindable settings properties.
- Partial `On<Property>Changed` methods in `UserSettings` handle side-effects (theme changes, taskbar updates).
- An `_initializing` flag in `UserSettings` suppresses change handlers during deserialization.
- P/Invoke declarations live in `NativeMethods.cs`. Always use `using static LittleLauncher.Classes.NativeMethods;` imports.
- Use `[LibraryImport]` for new P/Invoke declarations; existing ones use `[DllImport]`.
- Pages are WinUI 3 `Page` objects navigated via `NavigationView`. No MVVM framework routing — just `TargetPageType` in XAML.
- String resources live in `Resources/Localization/Dictionary-en-US.xaml`. In code: `Application.Current.Resources.TryGetValue("KeyName", out object value)`.
- Use `CommunityToolkit.Mvvm.Input.RelayCommand` for ICommand implementations.
- **MSIX VFS rule:** Any file path referenced by external processes (shell `.lnk` files, companion exe) must use `MainWindow.GetPhysicalAppDataDir()`, not raw `Environment.GetFolderPath(ApplicationData)`. The latter is VFS-redirected inside MSIX. See `icons.instructions.md` for details.

## Build

```bash
dotnet build LittleLauncher/LittleLauncher.csproj -c Debug
```

`Directory.Build.props` auto-detects the platform from `PROCESSOR_ARCHITECTURE` (ARM64 → ARM64, otherwise x64). To override: `-p:Platform=x64` or `-p:Platform=ARM64`.

Target: `net10.0-windows10.0.22000.0`, unpackaged (`WindowsPackageType=None`), platforms `x64` and `ARM64`.

Release builds AOT-publish the companion exe (`LauncherShortcut`) automatically. Debug builds copy the framework-dependent output for faster iteration.

## Dependencies

- Microsoft.WindowsAppSDK 1.8.260209005 (WinUI 3)
- H.NotifyIcon.WinUI 2.4.1 (system tray)
- CommunityToolkit.Mvvm 8.4.0
- SSH.NET 2025.1.0
- NLog 6.1.1

## Common tasks

- **Add a new settings page:** Create `Pages/FooPage.xaml` + `.cs`, add a `NavigationViewItem` in `SettingsWindow.xaml`, add any new string keys to `Dictionary-en-US.xaml`.
- **Add a new launcher feature:** Extend `LauncherItem` model, update `FlyoutWindow` to render it, update `LauncherItemsPage` for editing.
- **Add a new setting:** Add an `[ObservableProperty]` to `UserSettings.cs`. It will auto-serialize to JSON.

## Launcher item types

| Type | `IsWebsite` | `IsPwa` | `Path` | `Arguments` | Launch method |
|---|---|---|---|---|---|
| Website | `true` | `false` | URL | — | `UseShellExecute` or app-window mode |
| Application | `false` | `false` | exe path | optional args | `Process.Start(Path, Arguments)` |
| Progressive Web App | `false` | `true` | AUMID (e.g. `domain-HEX_hash!App`) | — | `explorer shell:AppsFolder\{Path}` |
| Heading | — | — | — | — | Not launchable (visual divider, renamed from Category) |
| Group | — | — | — | — | Not launchable (collapsible parent containing child items/headings via `Children` collection) |
| Column Break | — | — | — | — | Not launchable (splits the flyout into a new side-by-side column; `IsColumnBreak = true`) |

Groups have a `Children` (`ObservableCollection<LauncherItem>`) that holds nested items and headings. In the settings page, groups render as custom expand/collapse cards (StackPanel with `Tag="GroupRoot"` / `Tag="GroupChildren"`), not WinUI Expanders — this allows the entire group card to be a drag-and-drop source. `LauncherItem.IsExpanded` (`[JsonIgnore]`, defaults `true`) tracks the collapse state so it survives `RefreshList()` re-renders. In the flyout, the hierarchy is flattened for display.

Column breaks (`IsColumnBreak = true`) are structural dividers that cause the flyout to render a new side-by-side column. They are not displayed or launchable — they only affect flyout column layout. Created via `LauncherItem.CreateColumnBreak()`.

PWAs are auto-detected by enumerating `shell:AppsFolder` for Chromium-registered app entries (AUMIDs matching `{domain}-{HEX}_{hash}!App`). Icons are fetched from the PWA domain via `FaviconService.FetchAndCacheAsync()`.
- **Release a new version:** Edit `<Version>` in `Directory.Build.props`, update fallback version in `Package.wxs`, commit, tag `vX.Y.Z`, push. The MSIX manifest version is auto-stamped by `LittleLauncherMSIX/build-msix.ps1`. See `versioning.instructions.md` for the full checklist.
- **Update the app icon:** Replace `Resources/AppIcons/Blue.png`, then regenerate `Resources/LittleLauncher.ico` from it (multi-resolution ICO: 16–256px). The `.ico` is committed — it's not auto-generated by the build. See `icons.instructions.md`.

## Documentation maintenance

**Documentation updates are part of the task — a task is not done until docs are updated.**

After completing any feature, bug fix, or structural change, review and update the affected documentation before considering the task done. This includes:

| What changed | Update these |
|---|---|
| New/removed service or class | `copilot-instructions.md` (Key namespaces table), repo memory |
| New/changed settings property | `user-settings.instructions.md`, repo memory |
| New/changed P/Invoke | `pinvoke.instructions.md` |
| Icon system changes | `icons.instructions.md` (surfaces table, TrayIconMode table, gotchas) |
| Installer changes | `installer.instructions.md` |
| Version bump | `versioning.instructions.md` if the process changed; fallback versions in `Package.wxs` + `Package.appxmanifest` |
| New/changed XAML patterns | `xaml.instructions.md` |
| Drag-and-drop changes | `drag-drop.instructions.md` |
| New page or navigation change | `copilot-instructions.md` (Architecture section) |
| New dependency added/removed | `copilot-instructions.md` (Dependencies list) |
| Any structural change | `ARCHITECTURE.md`, `README.md` if affected |

**Rule:** If an instruction file's `applyTo` pattern wouldn't match the new files you created, widen the glob or add the new path. Instruction files that don't match are never loaded.

**Rule:** Read the relevant instruction file before deciding whether it needs updating — don't skip this based on assumptions.
