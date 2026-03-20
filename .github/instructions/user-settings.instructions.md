---
description: "Use when adding or modifying observable settings properties in UserSettings.cs, handling property change side-effects, or extending the serialized settings schema."
applyTo: "**/ViewModels/UserSettings*.cs"
---

# UserSettings Conventions

## Adding a New Setting

1. Add an `[ObservableProperty]` field (lowercase with underscore prefix):
   ```csharp
   [ObservableProperty]
   private bool _myNewFeature;
   ```
2. CommunityToolkit.Mvvm generates `MyNewFeature` property + `OnMyNewFeatureChanged` partial method
3. The property auto-serializes to XML via `XmlSerializer` — no extra config needed

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

## XML Serialization

- Properties marked `[XmlIgnore]` are excluded from settings.xml
- `ObservableCollection<T>` properties serialize as XML arrays
- Default values in field initializers are used when the element is missing from XML
- After deserialization, `CompleteInitialization()` is called to finalize state

## Non-Serialized Model Properties

`LauncherItem.IsExpanded` is `[XmlIgnore]` (defaults `true`) — it tracks the group expand/collapse state in the settings UI but is not persisted to disk. It is a plain property (not `[ObservableProperty]`) since it doesn't need data binding or change notification.

## Property Categories

Group related properties together with comment headers matching existing style:
- Appearance & Behaviour
- Taskbar Widget
- Launcher Items (includes `LauncherItems` collection and `SharedGroupSources`)
- SFTP Sync

## SharedGroupSources

`UserSettings.SharedGroupSources` is a `List<SharedGroupSource>` (not `ObservableCollection`). Each entry:
- Links to a `LauncherItem` group via matching `SharedGroupSource.Id` ↔ `LauncherItem.SharedGroupId`.
- `IsOwner = true` → this user publishes the group to a local file or SFTP path.
- `IsOwner = false` → this user subscribes; group children are replaced on sync.

When a shared group is removed from the launcher items, its `SharedGroupSource` must also be removed from this list (handled in `LauncherItemsPage.RemoveItem_Click`).
