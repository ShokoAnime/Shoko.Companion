using System;
using System.Collections.Generic;
using System.Net.Http;
using Newtonsoft.Json;
using NLog;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Configuration;

/// <summary>
/// Holds all per-server-connection data: display name, ordered list of routes,
/// API key, cached folder metadata, and per-connection preferences.
/// </summary>
public class ServerConnection
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Display name for this connection. Editable by the user.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Stable unique identifier for this connection. Used as a stable key
    /// for features like Media Session auto-connect, so renames don't
    /// break the reference.
    /// Generated automatically for new connections.
    /// </summary>
    [JsonProperty("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Ordered list of routes for reaching this server.
    /// At least one route is required. Routes are probed in order.
    /// </summary>
    public List<ConnectionRoute> Routes { get; set; } = [];

    /// <summary>
    /// API key for authenticating with this server.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Managed folder IDs the user has chosen to permanently ignore
    /// on this server. Future open-folder requests for these IDs
    /// are silently skipped.
    /// </summary>
    public HashSet<int> IgnoredManagedFolderIds { get; set; } = [];

    /// <summary>
    /// Cached managed folder metadata fetched from this server.
    /// Populated by <see cref="Server.ShokoApiClient.FetchManagedFoldersAsync"/>.
    /// </summary>
    [JsonIgnore]
    public List<ManagedFolderDto>? CachedManagedFolders { get; set; }

    /// <summary>
    /// Returns the display name.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>
    /// Local path mappings for this server's managed folders.
    /// Each maps a server-side managed folder ID to a local path.
    /// </summary>
    public List<ManagedFolderMapping> ManagedFolderMappings { get; set; } = [];

    // ── Session-level reachability cache ───────────────────────────

    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly Dictionary<string, string> WorkingUrlCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Get all candidate full base URLs for this connection, in route order.
    /// Each entry is a full URL (e.g. <c>http://myserver:8111</c>).
    /// </summary>
    public IEnumerable<string> GetAllBaseUrls()
    {
        foreach (var route in Routes)
        {
            if (!string.IsNullOrWhiteSpace(route.BaseUrl))
                yield return route.FullUrl;
        }
    }

    /// <summary>
    /// Probe routes in order and return the first reachable full base URL.
    /// Result is cached for the lifetime of the session.
    /// Returns null if no route is reachable.
    /// </summary>
    public string? ProbeReachableBaseUrl()
    {
        var key = Name; // cache keyed on connection name
        if (!string.IsNullOrWhiteSpace(key) && WorkingUrlCache.TryGetValue(key, out var cached))
            return cached;

        foreach (var candidate in GetAllBaseUrls())
        {
            var baseUrl = candidate.TrimEnd('/');
            var probeUrl = $"{baseUrl}/api/v3/Init/Version";

            try
            {
                var response = ProbeHttp.GetAsync(probeUrl)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                if (response.IsSuccessStatusCode)
                {
                    var body = response.Content.ReadAsStringAsync()
                        .ConfigureAwait(false).GetAwaiter().GetResult();

                    if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith("{"))
                    {
                        Logger.Info("Reachable via {Url}", baseUrl);
                        if (!string.IsNullOrWhiteSpace(key))
                            WorkingUrlCache[key] = baseUrl;
                        return baseUrl;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Unreachable via {Url}", baseUrl);
            }
        }

        Logger.Warn("No reachable URL found for connection {Name}", Name);
        return null;
    }

    /// <summary>
    /// Clear the session-level reachability cache.
    /// </summary>
    public static void ClearReachabilityCache()
    {
        WorkingUrlCache.Clear();
    }
}
