# LittleLauncherSetup (WiX MSI)

Per-user MSI built with WiX Toolset 5. `Package.wxs` and `LittleLauncherSetup.wixproj` define the install layout, Start Menu shortcut lifecycle, upgrade behavior, and uninstall cleanup.

@../.claude/docs/installer.md

`Package.wxs` carries the fallback `ProductVersion` define that must stay in sync on every version bump:

@../.claude/docs/versioning.md
