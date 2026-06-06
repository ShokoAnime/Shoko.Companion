using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Launch;
using Shoko.Companion.Notifications;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Playback;

/// <summary>
/// Result of resolving an absolute open-folder URL.
/// </summary>
public class OpenFolderAbsoluteResult
{
    /// <summary>Folder was opened in the OS file manager.</summary>
    public bool Opened { get; init; }

    /// <summary>A matching managed folder was found but needs a local mapping.</summary>
    public bool NeedsMapping { get; init; }

    /// <summary>No managed folder on the server matched the absolute path.</summary>
    public bool FolderNotFound { get; init; }

    /// <summary>The matching managed folder is in the user's ignore list.</summary>
    public bool IsPermanentlyIgnored { get; init; }

    /// <summary>The matched managed folder ID (set when NeedsMapping or Opened).</summary>
    public int? ManagedFolderId { get; init; }

    /// <summary>Server-side path of the matched managed folder.</summary>
    public string? ServerPath { get; init; }

    /// <summary>Display name of the matched managed folder.</summary>
    public string? ManagedFolderName { get; init; }
}

/// <summary>
/// Handles the "open-folder" action: looks up a <see cref="ManagedFolderMapping"/>
/// by its <see cref="ManagedFolderMapping.Id"/>, optionally appends a relative path,
/// and opens the result in the OS file manager.
/// Also handles the "open-folder-absolute" action by matching server-side absolute
/// paths against managed folder definitions, creating mappings when needed.
/// </summary>
public class FolderActionHandler
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Shared singleton instance.
    /// </summary>
    public static FolderActionHandler Instance { get; } = new();

    private readonly INotificationService _notifications;

    /// <summary>
    /// Initializes a new instance with the default notification service.
    /// </summary>
    public FolderActionHandler()
        : this(PlatformNotificationService.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified notification service.
    /// </summary>
    public FolderActionHandler(INotificationService notifications)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    }

    /// <summary>
    /// Look up the managed folder by <see cref="ParsedShokoUrl.ManagedFolderId"/>,
    /// combine its local path with <see cref="ParsedShokoUrl.RelativePath"/> (if set),
    /// and open the result in the OS file manager.
    /// </summary>
    /// <returns><c>true</c> if the folder was opened; <c>false</c> if the mapping
    /// was not found or is permanently ignored (caller should prompt the user).</returns>
    public bool OpenFolder(ParsedShokoUrl url)
    {
        if (!url.IsOpenFolderAction || url.ManagedFolderId is null)
        {
            Logger.Warn("OpenFolder called without a managed folder ID");
            return false;
        }

        var managedId = url.ManagedFolderId.Value;
        Logger.Info("Open-folder request for managed folder ID: {Id}", managedId);

        // Silently skip permanently ignored IDs for this host
        var settings = SettingsProvider.Instance.Settings;
        var routeKey = RouteResolver.ExtractRouteKey(url.ServerBaseUrl);
        var connection = routeKey is not null
            ? settings.GetConnectionByRouteKey(routeKey)
            : null;

        if (connection?.IgnoredManagedFolderIds.Contains(managedId) == true)
        {
            Logger.Info("Ignoring open-folder for ID {Id} on route {Route} (permanently ignored)", managedId, routeKey);
            return false;
        }

        var mapping = connection?.ManagedFolderMappings.FirstOrDefault(m => m.Id == managedId);
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.LocalPath))
        {
            Logger.Info("No local mapping for managed folder ID {Id} — caller should prompt", managedId);
            return false;
        }

        // Combine local path with optional relative path
        var fullPath = url.RelativePath is not null
            ? Path.Combine(mapping.LocalPath, url.RelativePath)
            : mapping.LocalPath;

        Logger.Info("Resolved managed folder #{Id} to local path: {Path}", managedId, fullPath);
        OpenInFileManager(fullPath);
        return true;
    }

    /// <summary>
    /// Resolve an absolute open-folder URL by fetching managed folders from the
    /// server, finding the one whose server-side <c>Path</c> is a prefix of the
    /// requested absolute path, then opening the mapped local path or returning
    /// a result indicating a mapping is needed.
    /// </summary>
    public async Task<OpenFolderAbsoluteResult> ResolveAbsolutePathAsync(ParsedShokoUrl url)
    {
        if (!url.IsOpenFolderAbsoluteAction)
        {
            Logger.Warn("ResolveAbsolutePathAsync called without an absolute path");
            return new() { FolderNotFound = true };
        }

        var absolutePath = url.AbsolutePath;
        Logger.Info("Open-folder-absolute request for path: {Path}", absolutePath);

        var settings = SettingsProvider.Instance.Settings;
        var routeKey = RouteResolver.ExtractRouteKey(url.ServerBaseUrl);
        var connection = routeKey is not null
            ? settings.GetConnectionByRouteKey(routeKey)
            : null;

        if (connection is null)
        {
            Logger.Warn("No connection found for route {Route}", routeKey);
            _notifications.Show("Open Folder Error",
                "No server connection found for this URL.", NotificationSeverity.Error);
            return new() { FolderNotFound = true };
        }

        // Fetch managed folders (use cache if available)
        var folders = await FetchManagedFoldersAsync(connection, url.ServerBaseUrl);
        if (folders is null || folders.Count == 0)
        {
            _notifications.Show("Open Folder Error",
                "Could not fetch managed folders from the server.", NotificationSeverity.Error);
            return new() { FolderNotFound = true };
        }

        // Find the best matching folder by longest path prefix
        var matched = FindMatchingManagedFolder(folders, absolutePath);
        if (matched is null)
        {
            Logger.Info("No managed folder path is a prefix of absolute path: {Path}", absolutePath);
            _notifications.Show("Open Folder Failed",
                $"No managed folder matches the path: {absolutePath}", NotificationSeverity.Error);
            return new() { FolderNotFound = true };
        }

        var managedId = matched.ID;
        Logger.Info("Matched managed folder #{Id} ({Name}) for path {Path}",
            managedId, matched.Name, absolutePath);

        // Check permanently ignored
        if (connection.IgnoredManagedFolderIds.Contains(managedId))
        {
            Logger.Info("Ignoring open-folder-absolute for ID {Id} (permanently ignored)", managedId);
            return new() { IsPermanentlyIgnored = true, ManagedFolderId = managedId };
        }

        // Check existing mapping
        var mapping = connection.ManagedFolderMappings.FirstOrDefault(m => m.Id == managedId);
        if (mapping is not null && !string.IsNullOrWhiteSpace(mapping.LocalPath))
        {
            var relativePart = absolutePath[matched.Path!.Length..].TrimStart('/');
            var fullLocalPath = string.IsNullOrWhiteSpace(relativePart)
                ? mapping.LocalPath
                : Path.Combine(mapping.LocalPath, relativePart);

            Logger.Info("Resolved absolute path to local path: {Path}", fullLocalPath);
            OpenInFileManager(fullLocalPath);
            return new() { Opened = true, ManagedFolderId = managedId };
        }

        // Needs a new mapping
        return new()
        {
            NeedsMapping = true,
            ManagedFolderId = managedId,
            ServerPath = matched.Path,
            ManagedFolderName = matched.Name
        };
    }

    /// <summary>
    /// Find the managed folder whose server-side <see cref="ManagedFolderDto.Path"/>
    /// is the longest matching prefix of <paramref name="absolutePath"/>.
    /// Returns null if no folder's path is a prefix.
    /// </summary>
    internal static ManagedFolderDto? FindMatchingManagedFolder(
        List<ManagedFolderDto> folders, string absolutePath)
    {
        return folders
            .Where(f => !string.IsNullOrWhiteSpace(f.Path))
            .OrderByDescending(f => f.Path!.Length)
            .FirstOrDefault(f => absolutePath.StartsWith(f.Path!, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolve managed folder metadata for a given server URL and folder ID.
    /// Fetches managed folders from the server if not already cached.
    /// </summary>
    /// <returns>The matching managed folder DTO, or null if not found.</returns>
    public async Task<ManagedFolderDto?> ResolveManagedFolderInfoAsync(string serverBaseUrl, int managedId)
    {
        var routeKey = RouteResolver.ExtractRouteKey(serverBaseUrl);
        var connection = routeKey is not null
            ? SettingsProvider.Instance.Settings.GetConnectionByRouteKey(routeKey)
            : null;
        if (connection is null)
            return null;

        var folders = await FetchManagedFoldersAsync(connection, serverBaseUrl);
        return folders?.FirstOrDefault(f => f.ID == managedId);
    }

    /// <summary>
    /// Fetch managed folders from the server or return cached ones.
    /// </summary>
    internal static async Task<List<ManagedFolderDto>?> FetchManagedFoldersAsync(
        ServerConnection connection, string? serverBaseUrl)
    {
        if (connection.CachedManagedFolders is { Count: > 0 })
            return connection.CachedManagedFolders;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var apiClient = new ShokoApiClient(http);

            var reachable = connection.ProbeReachableBaseUrl() ?? serverBaseUrl;
            if (string.IsNullOrWhiteSpace(reachable))
                return null;

            apiClient.SetBaseUrl(reachable.TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(connection.ApiKey))
                apiClient.SetApiKey(connection.ApiKey);

            var folders = await apiClient.FetchManagedFoldersAsync();
            if (folders is not null && connection.CachedManagedFolders is null)
            {
                connection.CachedManagedFolders = folders;
                SettingsProvider.Instance.Save();
            }
            return folders;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to fetch managed folders");
            return null;
        }
    }

    /// <summary>
    /// Extract the host:port portion from a server base URL.
    /// </summary>
    internal static string? ExtractHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        try
        {
            var uri = new Uri(baseUrl);
            return uri.Host + (uri.Port > 0 ? $":{uri.Port}" : string.Empty);
        }
        catch (UriFormatException ex)
        {
            Logger.Debug(ex, "Could not parse host from {BaseUrl}", baseUrl);
            return null;
        }
    }

    /// <summary>
    /// Open the specified path in the OS-default file manager.
    /// Falls back to a notification if the OS command fails.
    /// </summary>
    private void OpenInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer")
                {
                    UseShellExecute = true,
                    ArgumentList = { path }
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open")
                {
                    UseShellExecute = true,
                    ArgumentList = { path }
                });
            }
            else
            {
                // Linux / other Unix
                Process.Start(new ProcessStartInfo("xdg-open")
                {
                    UseShellExecute = true,
                    ArgumentList = { path }
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open file manager at {Path}", path);
            _notifications.Show(
                "Open Folder Failed",
                $"Could not open file manager:\n{ex.Message}",
                NotificationSeverity.Error);
        }
    }
}
