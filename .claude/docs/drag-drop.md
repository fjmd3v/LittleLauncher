> **Scope:** Use when modifying drag-and-drop behavior in the Launcher Items settings page. Covers the custom drag-drop system, cross-list moves, insertion indicators, and known WinUI 3 limitations.
> **Governs:** `**/LauncherItemsPage.xaml*` (`LittleLauncher/Pages/LauncherItemsPage.xaml` + `.cs`).

# Drag-and-Drop Conventions (LauncherItemsPage)

## Why custom drag-drop (not CanReorderItems)

WinUI 3's `CanReorderItems` takes full internal control of `DragOver` and `Drop` events. Its built-in handlers evaluate the drag source at a low level and **cannot be reliably overridden** — even `AddHandler` with `handledEventsToo` doesn't work consistently. This makes cross-list drag-drop (between top-level and group ListViews) impossible with `CanReorderItems`.

**Solution:** All ListViews use `CanDragItems="True"` (not `CanReorderItems`) with fully custom `DragOver`, `DragLeave`, `Drop`, `DragItemsStarting`, and `DragItemsCompleted` handlers.

## Architecture

### Multi-column layout

The settings page renders launcher items in a multi-column Grid (`ColumnsPanel`). The flat `CurrentItems` collection is split at `IsColumnBreak` sentinel items into per-column `ObservableCollection<LauncherItem>` lists (`_columnLists`). Each column gets its own `ListView` created in `RebuildColumns()`. When items are added/removed/reordered, `SyncColumnsToFlatList()` writes the column lists back to the flat collection, re-inserting column break sentinels between columns.

### Drag surfaces

| Surface | ListView | Source collection | Notes |
|---|---|---|---|
| Column items | Per-column ListView (Tag = column index) | `_columnLists[colIdx]` | Supports cross-column drag-drop |
| Group children | `GroupChildList` (inside DataTemplate) | `group.Children` | Rejects `IsGroup` drops (groups can't nest) |
| Top-level drop zone | `TopLevelDropZone` (Border) | — | Appears only when dragging FROM a group; drops append to last column |
| Inter-column drop zones | `_newColumnDropZones` (List\<Border\>) | — | Appear during any drag; one between each pair of columns plus one at each end. Tag = insert position in `_columnLists`. Dropping creates a new column at that position. |

### Shared state fields

- `_dragItem` — the `LauncherItem` being dragged
- `_dragSourceCollection` — the `ObservableCollection<LauncherItem>` the item came from
- `_lastIndicatorContainer` — the last `ListViewItem` with an insertion indicator border

### Drop index calculation

`GetDropIndex(ListView, DragEventArgs)` iterates item containers, comparing the cursor Y position against each container's vertical midpoint. Returns the index where the item should be inserted (Count = append to end).

**Critical:** When reordering within the same collection, removing the dragged item shifts subsequent items up by one. The drop handlers must adjust: if the original index was before the drop index, decrement `dropIndex` by 1 after removal. This applies to both `ColumnListView_Drop` and `GroupChildList_Drop`.

## Visual feedback

### Insertion indicators

`ShowInsertionIndicator(ListView, int)` sets an accent-colored 3px border on the `ListViewItem` at the target position. In list mode this is a horizontal line (top or bottom); in icon-grid mode it's a vertical line (left or right). In grid mode, a compensating negative padding is applied on the same side so the container's outer dimensions stay constant and the `ItemsWrapGrid` doesn't reflow. `ClearInsertionIndicator()` resets the border, padding, and margin via `_lastIndicatorContainer`.

### Drag captions

`DragUIOverride.Caption` shows contextual text:
- Top-level/group: "Move above {targetItem.Name}" or "Move to end"
- Top-level drop zone: "Move to top level"

### TopLevelDropZone

A `Border` with `AllowDrop="True"` that sits below the `ColumnsPanel` Grid. It collapses by default and becomes `Visible` in `GroupChildList_DragItemsStarting` (only when dragging from a group). Hidden again in `DragItemsCompleted` and `Drop` handlers.

## Group collapse state

Groups use a custom expand/collapse StackPanel (Tag `"GroupRoot"` / `"GroupChildren"`), not WinUI Expanders. `LauncherItem.IsExpanded` (`[XmlIgnore]`, defaults `true`) preserves collapse state across `RebuildColumns()` re-renders. The `GroupRoot_Loaded` handler reads `IsExpanded` and restores the collapsed visual + chevron glyph.

## Button order (item action buttons)

Left to right: **Move to…** → **Move up** → **Move down** → **Edit** → **Remove**. This order is consistent across LauncherItemTemplate and HeadingItemTemplate.

## Common pitfalls

1. **Never use `CanReorderItems`** for ListViews that need cross-list drag-drop. It swallows drag events.
2. **Always adjust drop index** when removing from the same collection before inserting — classic off-by-one.
3. **`RebuildColumns()` re-creates all containers** — any visual state (borders, expanded/collapsed) must be model-backed or restored in `Loaded` handlers.
4. **Groups cannot be dropped into other groups** — `GroupChildList_DragOver` rejects `IsGroup` items.
5. **Cross-column drag-drop** — when dropping between columns or between a column and a group, always call `SyncColumnsToFlatList()` before `SaveAndUpdateTaskbar()` to keep the flat backing list in sync with the column views.
6. **Column breaks are invisible in the settings UI** — they exist only as sentinel items in the flat `CurrentItems` list. The "Add Column" button appends one; "Remove Column" merges items into the previous column.
