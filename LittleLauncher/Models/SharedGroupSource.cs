// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace LittleLauncher.Models;

public enum SharedGroupSourceType
{
    LocalPath,
    Sftp
}

/// <summary>
/// Configuration for a shared launcher group — either a group being published
/// (IsOwner = true) or a group subscribed from another user (IsOwner = false).
///
/// The shared data is a serialized List&lt;LauncherItem&gt; written to a file at the
/// configured location. Only the group's Children list is stored — not the
/// group name itself, so each subscriber may use their own name.
///
/// Sync is always 1-way: owner → location. Subscribers read, never write.
/// </summary>
public class SharedGroupSource
{
    /// <summary>
    /// Unique ID linking this source to a LauncherItem group via LauncherItem.SharedGroupId.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>True = this user publishes; false = subscribed from another user.</summary>
    public bool IsOwner { get; set; }

    /// <summary>Where the shared group file is stored.</summary>
    public SharedGroupSourceType SourceType { get; set; }

    // ── Local path ──────────────────────────────────────────────────────────

    /// <summary>Full local file path to the shared group XML file.</summary>
    public string LocalPath { get; set; } = "";

    // ── SFTP ────────────────────────────────────────────────────────────────

    public string SftpHost { get; set; } = "";
    public int SftpPort { get; set; } = 22;
    public string SftpUsername { get; set; } = "";
    public string SftpPrivateKeyPath { get; set; } = "";

    /// <summary>Full remote file path (supports ~ for home directory).</summary>
    public string SftpRemotePath { get; set; } = "";

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Returns a human-readable description of the share destination.</summary>
    public string GetLocationDescription()
    {
        if (SourceType == SharedGroupSourceType.LocalPath)
            return string.IsNullOrWhiteSpace(LocalPath) ? "(no path set)" : LocalPath;

        if (string.IsNullOrWhiteSpace(SftpHost))
            return "(no host set)";

        return $"sftp://{SftpHost}:{SftpPort}{SftpRemotePath}";
    }
}
