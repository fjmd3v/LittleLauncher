# LauncherShortcut (companion exe)

`LittleLauncherFlyout.exe` is the companion exe pinned to the taskbar. It signals the main app via `PostMessage` and, in `--pin` mode, sets relaunch properties on its MessageBox HWND for taskbar pin identity. Icon loading, pin identity, and AUMID stamping are all part of the icon system:

@../.claude/docs/icons.md

See also the root [CLAUDE.md](../CLAUDE.md) (LauncherShortcut architecture bullet) for the command-line arguments and signaling protocol.
