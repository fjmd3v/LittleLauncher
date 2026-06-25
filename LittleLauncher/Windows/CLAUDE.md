# LittleLauncher/Windows

`FlyoutWindow` is the per-launcher popup. It uses a transparent backdrop and custom drag handlers. Follow the WinUI 3 XAML conventions:

@../../.claude/docs/xaml.md

Flyout item rendering, favicon/app-icon fetching, and `InvalidateItems()` are part of the icon pipeline — read [.claude/docs/icons.md](../../.claude/docs/icons.md) (and `FlyoutConverters.cs` guidance there) when changing how items or icons render.
