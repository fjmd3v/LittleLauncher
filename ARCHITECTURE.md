# Architecture — Little Launcher

## High-level flow

```
App.xaml  →  MainWindow (invisible, owns tray icon)
                ├── FlyoutWindow (launcher popup)
                └── SettingsWindow (WinUI 3 + NavigationView)
                      ├── HomePage
                      ├── LauncherItemsPage
                      ├── SyncPage
                      ├── SystemPage
                      └── AboutPage
```

## LauncherItemsPage multi-column layout and drag-and-drop

The Launcher Items settings page renders items in a multi-column `Grid` (`ColumnsPanel`). The flat `Items` collection is split at `IsColumnBreak` sentinel items into per-column `ObservableCollection<LauncherItem>` lists by `BuildColumnLists()`. Each column gets its own `ListView` (fixed 280px wide), with column headers showing "Column N" and a remove button (except column 1). Users add columns via "Add Column" and remove them via the column header delete button (which merges items into the previous column). Each item/group card has a `...` context menu button (visible on hover via Opacity toggling) with Move up/down, Move to…, Edit, and Remove actions.

Drag-and-drop supports cross-column and cross-group moves: items can be dragged between columns, between columns and group child lists, and out of groups via a drop zone. All ListViews use `CanDragItems="True"` with custom `DragOver`/`Drop` handlers — WinUI 3's `CanReorderItems` is intentionally avoided because it takes full internal control of drag events and cannot support cross-collection moves. After drag operations, `SyncColumnsToFlatList()` writes the column lists back to the flat collection. See `.github/instructions/drag-drop.instructions.md` for implementation details.

## Launch modes

By default, launching the app opens the Settings window. Silent mode (tray icon only, no Settings window) is used for Windows startup and companion exe cold-starts:

| Scenario | How it launches | Settings window? |
|---|---|---|
| Install / update / Start Menu / double-click exe | No special args | Yes |
| Windows startup (unpackaged) | Registry Run key with `--silent` | No |
| Windows startup (MSIX) | StartupTask → `ExtendedActivationKind.StartupTask` | No |
| Companion exe cold-start | `LittleLauncher.exe --silent` | No |
| Second instance (app already running) | Signals first instance via `PostMessage` → shows Settings | Yes |

## Settings persistence

- `UserSettings` (the ViewModel) is an `ObservableObject` with `[ObservableProperty]` attributes.
- `SettingsManager` (fully static) serialises it to JSON at `%AppData%\LittleLauncher\settings.json`.
- On first load, migrates from legacy `settings.xml` (XmlSerializer) to `settings.json` (System.Text.Json), renaming the old file to `.bak`.
- On startup, `RestoreSettings()` deserialises and calls `CompleteInitialization()` to enable change handlers.
- `SaveSettings()` is called on settings window close and after SFTP download.

## Launcher item icons

`FaviconService.FetchMissingItemIconsAsync(items)` is the **single pipeline** for fetching launcher item icons. It iterates the items and, for each one missing a valid local icon, fetches a favicon (websites), extracts a shell icon (PWAs via `IShellItemImageFactory`), or extracts an exe icon (apps). All entry points use this one method:

| Trigger | Caller |
|---|---|
| App startup (missing icons on disk) | `MainWindow.FetchMissingIconsOnStartupAsync()` |
| SFTP sync download | `SftpSyncService.DownloadLaunchersAsync()` |
| File import (Launcher Items page) | `LauncherItemsPage.ImportItems_Click()` |
| Manual add/edit | `DoFetch()` in add/edit dialog (calls `FaviconService` directly for the single item) |
| PWA add | PWA combo selection handler (extracts shell icon via `FaviconService.GetPwaIconFromShell`, falls back to web favicon) |

After bulk icon changes, callers invoke `FlyoutWindow.InvalidateItems()` so the flyout rebuilds its containers on the next toggle.

## SFTP sync

`SftpSyncService` provides static async methods:
- `UploadLaunchersAsync()` — serializes all launchers to JSON and uploads `launchers.json` via SFTP.
- `DownloadLaunchersAsync()` — downloads `launchers.json`, deserializes, replaces the local launchers collection, fetches missing icons via the unified pipeline, and saves. Falls back to legacy `launcher-items.xml` if `launchers.json` doesn't exist.
- `TestConnectionAsync()` — verifies SSH connectivity and SFTP access.

### Shared launcher sync

Individual launchers can be shared via local/network files or per-launcher SFTP connections (separate from the global sync). `Launcher.SharedSyncMode` controls the transport: 0 = File (local or UNC path), 1 = SFTP.
- **Owner** (`IsSharedOwner = true`): `ShareLauncherAsync()` pushes the launcher's items as `List<LauncherItem>` JSON.
- **Subscriber** (`IsSharedOwner = false`): `SyncSharedLauncherAsync()` pulls items (read-only).
- `VerifySharedLauncherAsync()` — validates the file/remote exists and is parseable (used before subscribing).
- `SyncAllSharedLaunchersAsync()` — batch syncs all shared launchers (owners push, subscribers pull). File-mode always syncs; SFTP-mode skips launchers without an auto-detectable SSH key.

The sharing UI lives in `LaunchersPage.xaml.cs`: "Share" button on unshared launcher cards, "Sync" and "Settings" buttons on shared cards, "Shared"/"Subscribed" badges, "Add Shared Launcher" subscribe dialog (with File/SFTP mode picker), and "Stop Sharing" via the share dialog's secondary button.

`AutoSyncService` manages automatic sync triggers:
- Downloads launchers on startup, then syncs shared launchers.
- Debounced upload (3 s) when items change.
- Periodic download on a configurable interval, followed by shared launcher sync.

Supports both private-key (`PrivateKeyFile`) and password-based authentication.

## Theme system

`ThemeManager` controls the app theme via WinUI 3's `ElementTheme` system:
- Sets `RequestedTheme` on the root `FrameworkElement` of each window.
- `IsDarkTheme()` reads the system foreground colour from a cached `UISettings` instance to detect light/dark mode.
- Theme 0 = system default, 1 = Light, 2 = Dark.

## Backdrop

- **SettingsWindow** uses `MicaBackdrop` (WinUI 3 built-in).
- **FlyoutWindow** uses a transparent backdrop for seamless integration.

## FlyoutWindow

The flyout popup dismisses when focus is lost via the WinUI `Activated` event (`Deactivated` state). It uses `WS_EX_TOOLWINDOW` to stay out of Alt-Tab and `OverlappedPresenter.CreateForContextMenu()` for borderless always-on-top presentation.

**Multi-column & multi-view layout**: The flyout renders items into a horizontal `ColumnsPanel` (a `StackPanel`). Each `LauncherItem` with `IsColumnBreak = true` starts a new column. The display mode is controlled by `Launcher.ViewMode`:
- **List view** (ViewMode = 0, default): Each column is a `ListView` (175 px wide) with icon + text side-by-side, using `ItemTemplateSelector` and `ItemContainerStyleSelector`.
- **Icon view** (ViewMode = 1): Each column is a dynamically created `ScrollViewer` containing icon tiles in a 3-column wrapping grid (260 px wide), with 32×32 icons and text below.

`RebuildColumnsPanel()` rebuilds all columns (icon grid or ListView) from scratch whenever items change. Window width scales: 175 px (list) or 260 px (icon) per column.

**Right-click context menu**: Right-clicking empty space in the flyout shows a `ContextFlyout` with a "Settings" option that dismisses the flyout and opens `SettingsWindow`.

## Companion exe (`LauncherShortcut`)

`LittleLauncherFlyout.exe` is a tiny companion binary pinned to the taskbar. Clicking it sends a `PostMessage` to the main app to show the flyout, then exits. Because it launches on every click, startup latency is critical:

- **Release builds** use Native AOT (`dotnet publish`) producing a single ~1.6 MB native binary with no .NET runtime dependency.
- **Debug builds** copy the framework-dependent output for fast iteration (startup is slower but build is faster).
- **Never add heavy dependencies** (NuGet packages, large frameworks) to the `LauncherShortcut` project — it must remain a minimal P/Invoke-only program.
- **Never run expensive work** (icon regeneration, shell notifications, file I/O) synchronously in the main app's `_wmShowFlyout` WndProc handler — it blocks the flyout from appearing.

### Companion exe deployment

At startup, `EnsureFlyoutShortcut()` copies the companion exe to `%AppData%\\LittleLauncher\\` and creates a Start Menu shortcut pointing to that copy. This is done for **all** build types (WiX, MSIX, unpackaged) so there is a single, consistent location for pinning. A `main-exe-path.txt` breadcrumb file is written alongside the companion exe so it can launch the main app with `--silent` if `FindWindow` fails (main app not running).

## MSIX packaging

`LittleLauncherMSIX/build-msix.ps1` produces an MSIX package for Microsoft Store distribution or sideloading. Key details:

- **Publishes with `-p:WindowsPackageType=MSIX`** to suppress the unpackaged-only auto-bootstrapper (`MICROSOFT_WINDOWSAPPSDK_BOOTSTRAP_AUTO_INITIALIZE`), which fails in a packaged context.
- **Declares `<PackageDependency>`** on `Microsoft.WindowsAppRuntime.1.8` so the framework package provides WinRT activation factories.
- **Copies compiled XAML (.xbf)** files manually from the RID build directory to the layout — `dotnet publish` omits them.
- **Version and architecture** are stamped from `Directory.Build.props` into the manifest at build time (`VERSION_PLACEHOLDER`, `ARCH_PLACEHOLDER`).
- **Image assets** in `LittleLauncherMSIX/Images/` use standard MRT naming qualifiers (e.g. `.scale-200.`, `.targetsize-48.`) and are indexed into `resources.pri` by `makepri`.
- **Companion exe** is deployed to `%AppData%\\LittleLauncher\\` at startup for all build types. See \"Companion exe\" section above.
- **`-NoSign` flag** skips all signing for Store uploads (Microsoft re-signs during ingestion). Without `-NoSign`, the script signs with a self-signed dev cert or a trusted PFX.
- **Update checks and toast notifications** are disabled in MSIX builds — the Store handles updates. The GitHub-based update UI on Home/About pages is hidden.
