---
description: "Use when adding or modifying observable settings properties in UserSettings.cs, Launcher model properties, handling property change side-effects, or extending the serialized settings schema."
applyTo: "{**/ViewModels/UserSettings*.cs,**/Models/Launcher.cs}"
---

# UserSettings Conventions

## Adding a New Setting

1. Add an `[ObservableProperty]` field (lowercase with underscore prefix):
   ```csharp
   [ObservableProperty]
   private bool _myNewFeature;
   ```
2. CommunityToolkit.Mvvm generates `MyNewFeature` property + `OnMyNewFeatureChanged` partial method
3. The property auto-serializes to JSON via `System.Text.Json` — no extra config needed

## Side-Effects

- Implement `partial void OnMyNewFeatureChanged(bool value)` for reactive changes
- Always check `_initializing` flag to skip logic during deserialization:
  ```csharp
  partial void OnMyNewFeatureChanged(bool value)
  {
      if (_initializing) return;
      // side-effect logic here
  }
  ```

## JSON Serialization

- Properties marked `[JsonIgnore]` are excluded from settings.json
- `ObservableCollection<T>` properties serialize as JSON arrays
- Default values in field initializers are used when the property is missing from JSON
- `DefaultIgnoreCondition = WhenWritingDefault` omits default-valued properties from the output
- After deserialization, `CompleteInitialization()` is called to finalize state

## Non-Serialized Model Properties

`LauncherItem.IsExpanded` is `[JsonIgnore]` (defaults `true`) — it tracks the group expand/collapse state in the settings UI but is not persisted to disk. It is a plain property (not `[ObservableProperty]`) since it doesn't need data binding or change notification.

## Launchers Collection

`UserSettings.Launchers` is an `ObservableCollection<Launcher>`. Each `Launcher` holds:
- `Id` (GUID string, readonly key)
- `Name` (`[ObservableProperty]`)
- `TrayIconMode` (`[ObservableProperty]`, `string` — uses `TrayIconModes` constants like `\"Composite\"`, `\"Blue\"`, etc. A `TrayIconModeJsonConverter` handles migration from legacy integer values)
- `CustomTrayIconPath` (`[ObservableProperty]`)
- `NIconHide` (`[ObservableProperty]`)
- `ViewMode` (`[ObservableProperty]`)
- `ShowTitle` (`[ObservableProperty]`, shows launcher name at top of flyout)
- `Items: ObservableCollection<LauncherItem>`

### Sharing Properties (plain auto-properties, not `[ObservableProperty]`)
- `IsShared` (bool) — whether this launcher participates in sharing
- `IsSharedOwner` (bool) — `true` = publisher, `false` = subscriber; only meaningful when `SharedTwoWay` is `false`
- `SharedTwoWay` (bool) — `true` = all participants push and pull (last save wins); `false` = 1-way (owner pushes, subscribers pull)
- `SharedSyncMode` (int) — 0 = File (local/network path), 1 = SFTP
- `SharedPath` (string) — file path (local/UNC) or SFTP remote path depending on mode
- `SharedSftpHost`, `SharedSftpPort` (int, default 22), `SharedSftpUsername`, `SharedSftpPrivateKeyPath` — SFTP connection fields (only used when `SharedSyncMode == 1`)
- `SharedSftpRemotePath` — legacy migration-only setter that populates `SharedPath` + sets SFTP mode on deserialization
- `IsFileSync`, `IsSftpSync` — `[JsonIgnore]` convenience properties derived from `SharedSyncMode`

**Migration**: On first run with old settings, `CompleteInitialization()` checks `Launchers.Count == 0` and migrates `LauncherItems` + `TrayIconMode`/`NIconHide`/`CustomTrayIconPath` into a "Default" launcher (legacy int `TrayIconMode` is converted via `TrayIconModes.FromLegacyInt()`). The legacy properties remain in the schema but are not observable. On first load, migrates from legacy `settings.xml` to `settings.json`. The `TrayIconModeJsonConverter` on `Launcher.TrayIconMode` also handles reading legacy integer values from old JSON files.

**Do not** add `[ObservableProperty]` to the legacy migration fields (`LauncherItems`, `TrayIconMode`, `NIconHide`, `CustomTrayIconPath` on `UserSettings`) — they are plain migration-only properties marked with `[JsonIgnore]`.

## Property Categories

Group related properties together with comment headers matching existing style:
- Appearance & Behaviour
- Taskbar Widget
- Launchers
- SFTP Sync


