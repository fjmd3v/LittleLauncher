# LittleLauncher/Pages

Settings pages are WinUI 3 `Page` objects navigated via `NavigationView`. Follow the XAML conventions for all `.xaml` here.

@../../.claude/docs/xaml.md

`LauncherItemsPage` uses a fully custom drag-and-drop system — **never `CanReorderItems`**. Read this before touching any drag/drop or column logic:

@../../.claude/docs/drag-drop.md

`LaunchersPage` drives per-launcher tray/pin icons. When editing it, follow the icon system conventions: [.claude/docs/icons.md](../../.claude/docs/icons.md).
