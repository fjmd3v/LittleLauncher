> **Scope:** Use when editing XAML files for WinUI 3 controls, Fluent Design, NavigationView pages, or resource dictionaries. Covers WinUI 3 control conventions, resource localization, and Mica/Acrylic backdrop patterns.
> **Governs:** `**/*.xaml` (all XAML across the solution).

# WinUI 3 XAML Conventions

## Namespaces

Standard WinUI 3 pages use these default namespaces:
```xml
xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
```

## Localization

- String resources live in `Resources/Localization/Dictionary-en-US.xaml`
- Reference via `{StaticResource KeyName}` in XAML (resource dictionary is merged in App.xaml)
- In code: `Application.Current.Resources.TryGetValue("KeyName", out object value)`
- Always add new string keys to the dictionary when adding UI text

## Pages

- Pages are `Page` objects (not `UserControl`)
- Navigation via WinUI 3 `NavigationView` with `TargetPageType` in XAML
- No MVVM routing framework — direct page type references

## Controls

- Use `Border` with `{ThemeResource CardBackgroundFillColorDefaultBrush}` and `{ThemeResource CardStrokeColorDefaultBrush}` for settings cards
- Use `ToggleSwitch` for boolean settings
- Use `NumberBox` for numeric inputs
- Use `FontIcon` with Segoe Fluent Icons glyphs

## Data Binding

- Bind to `SettingsManager.Current.<Property>` for settings
- Use `Mode=TwoWay` for editable settings
- Use `UpdateSourceTrigger=PropertyChanged` when immediate feedback needed

## Backdrops

- **SettingsWindow** uses `MicaBackdrop`
- **FlyoutWindow** uses a transparent backdrop

## Code-Built Dialogs

The add/edit item dialog in `LauncherItemsPage` is built entirely in C# (not XAML). Conventions:

- All input controls (`TextBox`, `ComboBox`, `ToggleSwitch`) use `HorizontalAlignment = HorizontalAlignment.Stretch` to fill the dialog width uniformly.
- The form container is a `StackPanel` with `MinWidth = 460`, wrapped in a `ScrollViewer` (`MaxHeight = 620`) so a tall form never overflows off-screen.
- When a row needs a stretch input + a fixed button (e.g. path + Browse), use a `Grid` with `Star` + `Auto` column definitions instead of a horizontal `StackPanel`.
- Labels are created via a `Label(string)` helper that returns a styled `TextBlock`.

### Unified app/PWA picker

There is **no type dropdown**. The target is chosen via a two-item `SelectorBar` tab strip — modelled on the picker in the sibling `CopilotRekey` project:

- **"Apps & web apps"** tab — a single searchable list (`AutoSuggestBox` + `ListView`, fixed `Height = 260`) showing installed applications and PWAs together, each with an icon.
- **"File or link"** tab — the manual path/link `TextBox` + Browse, the arguments box, and the website "Open as app window" toggle + browser/profile pickers.

`Name` and `Icon` live below the tab content (shared across both tabs). `ShowTabPanel(tag)` toggles the two panels' visibility, keeps `tabBar.SelectedItem` in sync, and re-derives the target; the `SelectorBar.SelectionChanged` handler routes through it (a `populating` flag guards re-entrancy, same pattern as `_initializing` in `UserSettings`).

- The list is fed by `AppPickerEntry` rows (display name, `LaunchPath`, `IsPwa`, icon target). The catalog combines `GetInstalledApplications()` + `GetInstalledPwas()` and is built on a **background STA thread** via `AppPickerService.RunStaAsync` (the shell enumeration is expensive and apartment-threaded), so the dialog opens instantly and the list fills in.
- Icons stream in asynchronously via `AppPickerService.LoadIcons` → `ShellIcons.Extract` (an `IShellItemImageFactory` 32px BGRA extraction). The `ListView` `DataTemplate` is built from a XAML string with `XamlReader.Load` and binds `{Binding Icon}` (`ObservableObject` change notification), since the dialog is code-built.
- **Target resolution:** `ResolveTarget()` keys off the active tab — `custom` → the typed path/link (classified by `LooksLikeWebUrl` / `LooksLikeFilePath` into website vs application); `list` → the selected `AppPickerEntry`. It returns `(path, isPwa, isWebsite)`; `SyncDerived()` pushes that into the derived flags and the app-window options' visibility.
- Stored values are unchanged from before (PWA → AUMID in `Path` + `IsPwa`; Store app → `shell:AppsFolder\…` path; exe → file path; website URL → `IsWebsite`), so launch behaviour in `FlyoutWindow` is preserved.

## Drag-and-Drop (LauncherItemsPage)

ListViews use `CanDragItems="True"` with custom handlers — **never `CanReorderItems`**, which cannot be overridden for cross-list drops. See [drag-drop.md](drag-drop.md) for full details.

## Group Expand/Collapse

Groups use a manual `StackPanel` with `Tag="GroupRoot"` / `Tag="GroupChildren"` and a toggle button — **not WinUI Expanders**. This allows the entire group card to be a drag source. The `Loaded` event on `GroupRoot` restores `IsExpanded` state after `RefreshList()` rebuilds the visual tree.
