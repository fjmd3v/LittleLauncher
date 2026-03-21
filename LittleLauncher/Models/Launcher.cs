// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace LittleLauncher.Models;

/// <summary>
/// Represents a named launcher: a set of launcher items with their own tray icon
/// appearance and a stable GUID-based identity for cross-process routing.
/// </summary>
public partial class Launcher : ObservableObject
{
    /// <summary>
    /// Stable GUID-based identifier.
    /// Used as the key for per-launcher tray icons, flyout windows, and companion shortcuts.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Display name shown in the tray icon tooltip and the settings Launchers page.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "Launcher";

    /// <summary>
    /// Tray icon style for this launcher.
    /// 0-5 = Color rockets (Blue/Green/Teal/Red/Orange/Purple),
    /// 6-11 = Glyphs (Pin/Star/Heart/Lightning/Search/Globe), 12 = Custom image.
    /// </summary>
    [ObservableProperty]
    public partial int TrayIconMode { get; set; }

    /// <summary>Path to a custom tray icon file (.ico or .png) when TrayIconMode is 12 (Custom).</summary>
    [ObservableProperty]
    public partial string CustomTrayIconPath { get; set; } = "";

    /// <summary>When true, this launcher's tray icon is hidden from the system tray.</summary>
    [ObservableProperty]
    public partial bool NIconHide { get; set; }

    /// <summary>
    /// The launcher items (shortcuts, groups, headings, column breaks) in this launcher.
    /// Serialized as a JSON array.
    /// </summary>
    public ObservableCollection<LauncherItem> Items { get; set; } = [];

    // ── Sharing ─────────────────────────────────────────────────────

    /// <summary>Whether this launcher participates in per-launcher sharing.</summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// true = this user publishes (owner); false = subscribed (consumer, items read-only).
    /// Only meaningful when <see cref="IsShared"/> is true.
    /// </summary>
    public bool IsSharedOwner { get; set; }

    /// <summary>
    /// 0 = File (local or network path), 1 = SFTP.
    /// Determines how the shared launcher items are read/written.
    /// </summary>
    public int SharedSyncMode { get; set; }

    /// <summary>
    /// The file path (local/network) or SFTP remote path for the shared launcher JSON.
    /// For File mode: a local or UNC path (e.g. C:\shared\launcher.json or \\server\share\launcher.json).
    /// For SFTP mode: a remote path (supports ~ for home directory).
    /// </summary>
    public string SharedPath { get; set; } = "";

    /// <summary>SFTP host for the shared launcher file (SFTP mode only).</summary>
    public string SharedSftpHost { get; set; } = "";

    /// <summary>SFTP port for the shared launcher file (SFTP mode only).</summary>
    public int SharedSftpPort { get; set; } = 22;

    /// <summary>SFTP username — defaults to Windows username if empty (SFTP mode only).</summary>
    public string SharedSftpUsername { get; set; } = "";

    /// <summary>Path to a private key for the shared SFTP connection — auto-detected if empty (SFTP mode only).</summary>
    public string SharedSftpPrivateKeyPath { get; set; } = "";

    /// <summary>
    /// Legacy property for backward compatibility with settings saved before SharedPath was introduced.
    /// On deserialization, migrates its value into <see cref="SharedPath"/> and sets SFTP mode.
    /// </summary>
    public string SharedSftpRemotePath
    {
        get => "";
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(SharedPath))
            {
                SharedPath = value;
                SharedSyncMode = 1; // SFTP
            }
        }
    }

    /// <summary>Convenience: true when SharedSyncMode is File (0).</summary>
    [JsonIgnore]
    public bool IsFileSync => SharedSyncMode == 0;

    /// <summary>Convenience: true when SharedSyncMode is SFTP (1).</summary>
    [JsonIgnore]
    public bool IsSftpSync => SharedSyncMode == 1;

    // ── Constructor (defaults for JsonSerializer) ────────────────────

    public Launcher()
    {
        Name = "Launcher";
        TrayIconMode = 0;
        CustomTrayIconPath = "";
        NIconHide = false;
        Items = [];
    }

    // ── Factory ─────────────────────────────────────────────────────

    /// <summary>Creates a new launcher with sample starter items.</summary>
    internal static Launcher CreateDefault()
    {
        var launcher = new Launcher { Id = Guid.NewGuid().ToString(), Name = "Default" };
        launcher.Items.Add(new LauncherItem("Google", "https://www.google.com", "\uE774", isWebsite: true));
        launcher.Items.Add(new LauncherItem("Explorer", "explorer.exe", "Folder24"));
        launcher.Items.Add(new LauncherItem("Notepad", "notepad.exe", "Notepad24"));
        return launcher;
    }
}
