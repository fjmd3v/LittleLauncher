// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleLauncher.Models;

/// <summary>String constants for <see cref="Launcher.TrayIconMode"/>.</summary>
public static class TrayIconModes
{
    public const string Blue = "Blue";
    public const string Green = "Green";
    public const string Teal = "Teal";
    public const string Red = "Red";
    public const string Orange = "Orange";
    public const string Purple = "Purple";
    public const string Pin = "Pin";
    public const string Star = "Star";
    public const string Heart = "Heart";
    public const string Lightning = "Lightning";
    public const string Search = "Search";
    public const string Globe = "Globe";
    public const string Custom = "Custom";
    public const string Composite = "Composite";

    /// <summary>Prefix for gallery-chosen glyph/emoji icons stored in TrayIconMode.</summary>
    public const string GlyphPrefix = "Glyph:";

    /// <summary>Returns true if the mode is a gallery-chosen glyph/emoji (starts with "Glyph:").</summary>
    public static bool IsGlyphMode(string? mode) => mode?.StartsWith(GlyphPrefix, StringComparison.Ordinal) == true;

    /// <summary>Extracts the glyph character(s) from a "Glyph:X" or "Glyph:#RRGGBB:X" mode string.</summary>
    public static string? GetGlyphCharacter(string? mode)
    {
        if (!IsGlyphMode(mode)) return null;
        string payload = mode![GlyphPrefix.Length..];
        // If payload starts with "#", color is encoded: "#RRGGBB:glyph"
        if (payload.StartsWith('#') && payload.Length > 8 && payload[7] == ':')
            return payload[8..];
        return payload;
    }

    /// <summary>Extracts the optional hex color from a "Glyph:#RRGGBB:X" mode string. Returns null if no color.</summary>
    public static string? GetGlyphColor(string? mode)
    {
        if (!IsGlyphMode(mode)) return null;
        string payload = mode![GlyphPrefix.Length..];
        if (payload.StartsWith('#') && payload.Length > 8 && payload[7] == ':')
            return payload[..7]; // "#RRGGBB"
        return null;
    }

    /// <summary>Creates a TrayIconMode string for an arbitrary glyph character with optional color.</summary>
    public static string ToGlyphMode(string glyph, string? color = null) =>
        string.IsNullOrEmpty(color)
            ? GlyphPrefix + glyph
            : GlyphPrefix + color + ":" + glyph;

    /// <summary>Maps legacy integer TrayIconMode values to string constants.</summary>
    internal static string FromLegacyInt(int mode) => mode switch
    {
        0 => Blue, 1 => Green, 2 => Teal, 3 => Red, 4 => Orange, 5 => Purple,
        6 => Pin, 7 => Star, 8 => Heart, 9 => Lightning, 10 => Search, 11 => Globe,
        12 => Custom, 13 => Composite,
        _ => Blue,
    };
}

/// <summary>Integer constants for <see cref="Launcher.ViewMode"/>.</summary>
public static class LauncherViewModes
{
    public const int Icon = 0;
    public const int List = 1;
    public const int SmallIcon = 2;

    public static int Normalize(int value) => value switch
    {
        List => List,
        SmallIcon => SmallIcon,
        _ => Icon,
    };

    public static bool IsIconMode(int value) => Normalize(value) != List;
}

/// <summary>
/// Reads TrayIconMode as a string. If the JSON token is a number (legacy format),
/// converts it via <see cref="TrayIconModes.FromLegacyInt"/>.
/// </summary>
public class TrayIconModeJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return TrayIconModes.FromLegacyInt(reader.GetInt32());
        return reader.GetString() ?? TrayIconModes.Blue;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// Represents a named launcher: a set of launcher items with their own tray icon
/// appearance and a stable GUID-based identity for cross-process routing.
/// </summary>
public partial class Launcher : ObservableObject
{
    public const int MinIconModeIconsPerRow = 1;
    public const int DefaultIconModeIconsPerRow = 3;
    public const int MaxIconModeIconsPerRow = 12;

    public static int ClampIconModeIconsPerRow(int value) => Math.Clamp(value, MinIconModeIconsPerRow, MaxIconModeIconsPerRow);

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
    /// Use <see cref="TrayIconModes"/> constants: "Blue", "Green", "Teal", "Red", "Orange", "Purple",
    /// "Pin", "Star", "Heart", "Lightning", "Search", "Globe", "Custom", "Composite".
    /// </summary>
    [ObservableProperty]
    [JsonConverter(typeof(TrayIconModeJsonConverter))]
    public partial string TrayIconMode { get; set; } = TrayIconModes.Composite;

    /// <summary>Path to a custom tray icon file (.ico or .png) when TrayIconMode is "Custom".</summary>
    [ObservableProperty]
    public partial string CustomTrayIconPath { get; set; } = "";

    /// <summary>When true, this launcher's tray icon is hidden from the system tray.</summary>
    [ObservableProperty]
    public partial bool NIconHide { get; set; }

    /// <summary>
    /// Flyout display mode for this launcher.
    /// 0 = Icon (larger icon with name below, wrapping grid),
    /// 1 = List (icon + name side by side),
    /// 2 = Small Icon (tray-sized icon grid with no item labels).
    /// </summary>
    [ObservableProperty]
    public partial int ViewMode { get; set; }

    /// <summary>
    /// Number of icons shown across each icon-mode column.
    /// Defaults to 3 and is clamped to a sensible 1-12 range.
    /// </summary>
    [ObservableProperty]
    public partial int IconModeIconsPerRow { get; set; } = DefaultIconModeIconsPerRow;

    /// <summary>When true, the launcher name is shown at the top of the flyout popup.</summary>
    [ObservableProperty]
    public partial bool ShowTitle { get; set; }

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
    /// Only meaningful when <see cref="IsShared"/> is true and <see cref="SharedTwoWay"/> is false.
    /// </summary>
    public bool IsSharedOwner { get; set; }

    /// <summary>
    /// When true, all participants both push and pull (last save wins).
    /// When false, sharing is 1-way: owners push, subscribers pull.
    /// </summary>
    public bool SharedTwoWay { get; set; }

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
        TrayIconMode = TrayIconModes.Composite;
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
