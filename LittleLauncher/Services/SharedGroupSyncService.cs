// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Dispatching;
using Renci.SshNet;
using System.Collections.ObjectModel;
using System.IO;
using System.Xml.Serialization;

namespace LittleLauncher.Services;

/// <summary>
/// Handles sync of shared launcher groups.
/// - Owner mode (IsOwner = true): serializes the group's Children and pushes to the configured location.
/// - Subscriber mode (IsOwner = false): reads the file at the configured location and replaces the group's Children.
///
/// Sync is always 1-way (owner → file). Subscribers never write back.
/// </summary>
public static class SharedGroupSyncService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // ── Batch helpers ────────────────────────────────────────────────────────

    /// <summary>Sync all outgoing (owned) shared groups silently.</summary>
    public static async Task SyncAllOutgoingAsync()
    {
        var sources = SettingsManager.Current.SharedGroupSources;
        var items = SettingsManager.Current.LauncherItems;

        foreach (var source in sources.Where(s => s.IsOwner).ToList())
        {
            var group = FindGroupById(items, source.Id);
            if (group == null) continue;

            var (success, message) = await SyncOutgoingAsync(source, group);
            if (!success)
                Logger.Warn($"Outgoing sync failed for group {source.Id}: {message}");
            else
                Logger.Info($"Outgoing sync complete for shared group: {group.Name}");
        }
    }

    /// <summary>Sync all incoming (subscribed) shared groups silently.</summary>
    public static async Task SyncAllIncomingAsync()
    {
        var sources = SettingsManager.Current.SharedGroupSources;
        bool anyUpdated = false;

        foreach (var source in sources.Where(s => !s.IsOwner).ToList())
        {
            var (success, message) = await SyncIncomingAsync(source);
            if (!success)
                Logger.Warn($"Incoming sync failed for group {source.Id}: {message}");
            else
            {
                Logger.Info($"Incoming sync complete for subscribed group (id={source.Id})");
                anyUpdated = true;
            }
        }

        if (anyUpdated)
            SettingsManager.SaveSettings();
    }

    // ── Per-group operations ─────────────────────────────────────────────────

    /// <summary>
    /// Push a group's children to the configured destination.
    /// Returns (true, description) on success or (false, error) on failure.
    /// </summary>
    public static async Task<(bool Success, string Message)> SyncOutgoingAsync(
        SharedGroupSource source, LauncherItem group, string? sftpPassword = null)
    {
        try
        {
            byte[] data = SerializeChildren(group.Children);

            if (source.SourceType == SharedGroupSourceType.LocalPath)
            {
                if (string.IsNullOrWhiteSpace(source.LocalPath))
                    return (false, "No local file path configured.");

                string? dir = Path.GetDirectoryName(source.LocalPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllBytesAsync(source.LocalPath, data);
                return (true, $"Synced to {source.LocalPath}");
            }
            else
            {
                using var client = CreateSftpClient(source, sftpPassword);
                await Task.Run(() => client.Connect());

                string expandedPath = ExpandHome(client, source.SftpRemotePath);
                string? remoteDir = RemoteDirOf(expandedPath);
                if (!string.IsNullOrEmpty(remoteDir))
                    await Task.Run(() => EnsureRemoteDirectory(client, remoteDir));

                using var stream = new MemoryStream(data);
                await Task.Run(() => client.UploadFile(stream, expandedPath, canOverride: true));
                client.Disconnect();

                return (true, $"Synced to {source.SftpHost}:{source.SftpRemotePath}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Outgoing sync failed for shared group {source.Id}");
            return (false, $"Sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pull the shared group file from the configured source and replace the matching
    /// group's Children in the current LauncherItems collection.
    /// </summary>
    public static async Task<(bool Success, string Message)> SyncIncomingAsync(
        SharedGroupSource source, string? sftpPassword = null)
    {
        try
        {
            List<LauncherItem>? children;

            if (source.SourceType == SharedGroupSourceType.LocalPath)
            {
                if (string.IsNullOrWhiteSpace(source.LocalPath))
                    return (false, "No local file path configured.");

                if (!File.Exists(source.LocalPath))
                    return (false, $"Shared group file not found: {source.LocalPath}");

                byte[] bytes = await File.ReadAllBytesAsync(source.LocalPath);
                using var stream = new MemoryStream(bytes);
                children = DeserializeChildren(stream);
            }
            else
            {
                using var client = CreateSftpClient(source, sftpPassword);
                await Task.Run(() => client.Connect());

                string expandedPath = ExpandHome(client, source.SftpRemotePath);
                if (!await Task.Run(() => client.Exists(expandedPath)))
                {
                    client.Disconnect();
                    return (false, "Shared group file not found on remote server.");
                }

                using var ms = new MemoryStream();
                await Task.Run(() => client.DownloadFile(expandedPath, ms));
                ms.Position = 0;
                client.Disconnect();
                children = DeserializeChildren(ms);
            }

            if (children == null)
                return (false, "Failed to parse shared group file.");

            await ApplyChildrenAsync(source.Id, children);
            return (true, "Shared group updated.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Incoming sync failed for shared group {source.Id}");
            return (false, $"Sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifies that the configured location is reachable and contains a valid shared group file.
    /// Returns (true, itemCount, "") on success or (false, 0, errorMessage) on failure.
    /// </summary>
    public static async Task<(bool Success, int ItemCount, string Error)> VerifySourceAsync(
        SharedGroupSource source, string? sftpPassword = null)
    {
        try
        {
            List<LauncherItem>? children;

            if (source.SourceType == SharedGroupSourceType.LocalPath)
            {
                if (string.IsNullOrWhiteSpace(source.LocalPath))
                    return (false, 0, "No file path specified.");

                if (!File.Exists(source.LocalPath))
                    return (false, 0, "File not found.");

                byte[] bytes = await File.ReadAllBytesAsync(source.LocalPath);
                using var stream = new MemoryStream(bytes);
                children = DeserializeChildren(stream);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(source.SftpHost))
                    return (false, 0, "No SFTP host specified.");

                using var client = CreateSftpClient(source, sftpPassword);
                await Task.Run(() => client.Connect());

                string expandedPath = ExpandHome(client, source.SftpRemotePath);
                if (!await Task.Run(() => client.Exists(expandedPath)))
                {
                    client.Disconnect();
                    return (false, 0, "File not found on server.");
                }

                using var ms = new MemoryStream();
                await Task.Run(() => client.DownloadFile(expandedPath, ms));
                ms.Position = 0;
                client.Disconnect();
                children = DeserializeChildren(ms);
            }

            if (children == null)
                return (false, 0, "File exists but could not be parsed.");

            return (true, children.Count, "");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    // ── Lookup ───────────────────────────────────────────────────────────────

    /// <summary>Finds the group item whose SharedGroupId matches the given source ID.</summary>
    public static LauncherItem? FindGroupById(IEnumerable<LauncherItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.IsGroup && item.SharedGroupId == id)
                return item;
        }
        return null;
    }

    /// <summary>
    /// Returns true if an SSH key can be auto-resolved for the given source,
    /// meaning no password prompt is required.
    /// </summary>
    public static bool HasAutoKey(SharedGroupSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.SftpPrivateKeyPath))
            return File.Exists(source.SftpPrivateKeyPath);

        string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (!Directory.Exists(sshDir)) return false;

        foreach (var name in new[] { "id_ed25519", "id_rsa", "id_ecdsa", "id_dsa" })
        {
            if (File.Exists(Path.Combine(sshDir, name)))
                return true;
        }
        return false;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static byte[] SerializeChildren(ObservableCollection<LauncherItem> children)
    {
        var list = new List<LauncherItem>(children);
        using var stream = new MemoryStream();
        var serializer = new XmlSerializer(typeof(List<LauncherItem>));
        serializer.Serialize(stream, list);
        return stream.ToArray();
    }

    private static List<LauncherItem>? DeserializeChildren(Stream stream)
    {
        try
        {
            var serializer = new XmlSerializer(typeof(List<LauncherItem>));
            return serializer.Deserialize(stream) as List<LauncherItem>;
        }
        catch
        {
            return null;
        }
    }

    private static async Task ApplyChildrenAsync(string sourceId, List<LauncherItem> newChildren)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher != null)
        {
            Apply();
        }
        else
        {
            var tcs = new TaskCompletionSource();
            App.MainDispatcherQueue.TryEnqueue(() => { Apply(); tcs.SetResult(); });
            await tcs.Task;
        }

        void Apply()
        {
            var group = FindGroupById(SettingsManager.Current.LauncherItems, sourceId);
            if (group == null) return;
            group.Children.Clear();
            foreach (var child in newChildren)
                group.Children.Add(child);
        }
    }

    private static SftpClient CreateSftpClient(SharedGroupSource source, string? password)
    {
        if (string.IsNullOrWhiteSpace(source.SftpHost))
            throw new InvalidOperationException("SFTP host is not configured.");

        string username = string.IsNullOrWhiteSpace(source.SftpUsername)
            ? Environment.UserName
            : source.SftpUsername;

        string? keyPath = ResolveKeyPath(source.SftpPrivateKeyPath);
        if (keyPath != null)
        {
            var keyFile = string.IsNullOrEmpty(password)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, password);
            var keyAuth = new PrivateKeyAuthenticationMethod(username, keyFile);
            var connInfo = new ConnectionInfo(source.SftpHost, source.SftpPort, username, keyAuth);
            return new SftpClient(connInfo);
        }

        if (!string.IsNullOrEmpty(password))
            return new SftpClient(source.SftpHost, source.SftpPort, username, password);

        throw new InvalidOperationException(
            "No SSH key found and no password provided. " +
            "Place a key in ~/.ssh/ or specify a path.");
    }

    private static string? ResolveKeyPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return configured;
            Logger.Warn($"Configured SSH key not found: {configured}");
            return null;
        }

        string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (!Directory.Exists(sshDir)) return null;

        foreach (var name in new[] { "id_ed25519", "id_rsa", "id_ecdsa", "id_dsa" })
        {
            string candidate = Path.Combine(sshDir, name);
            if (File.Exists(candidate))
            {
                Logger.Info($"Auto-detected SSH key: {candidate}");
                return candidate;
            }
        }
        return null;
    }

    private static string ExpandHome(SftpClient client, string path)
    {
        if (path.StartsWith('~'))
            return client.WorkingDirectory.TrimEnd('/') + path[1..];
        return path;
    }

    private static string? RemoteDirOf(string path)
    {
        int idx = path.LastIndexOf('/');
        if (idx <= 0) return null;
        return path[..idx];
    }

    private static void EnsureRemoteDirectory(SftpClient client, string path)
    {
        string current = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            try { client.CreateDirectory(current); } catch { }
        }

        if (!client.Exists(path))
            throw new InvalidOperationException($"Failed to create remote directory: {path}");
    }
}
