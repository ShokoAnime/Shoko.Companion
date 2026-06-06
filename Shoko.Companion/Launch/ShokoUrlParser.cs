using System;
using System.Net.Http;
using System.Web;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Notifications;

namespace Shoko.Companion.Launch;

/// <summary>
/// Parses <c>shoko:</c> URLs into structured intent actions.
/// Handles protocol detection, route-based connection lookup,
/// and connection auto-creation for new servers.
///
/// Detection works by checking the end of the path against known action suffixes:
///   <c>/play</c> — Play
///   <c>/open-folder</c> — OpenFolder (relative) or OpenFolderAbsolute (by path)
///
/// The <c>/open-folder</c> suffix supports two modes:
///   Relative: <c>?managedFolder=&lt;id&gt;[&amp;relativePath=...]</c> → <see cref="ShokoUrlAction.OpenFolderRelative"/>
///   Absolute: <c>?path=/server/absolute/path</c> → <see cref="ShokoUrlAction.OpenFolderAbsolute"/>
///
/// Full form:
///   shoko:[//][protocol://]host[/sub/path]/&lt;suffix&gt;?params
/// </summary>
public static class ShokoUrlParser
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>
    /// Known action suffixes, ordered longest-first so more specific patterns
    /// (like /open-folder) match before shorter ones (like /play).
    /// </summary>
    private static readonly (string Suffix, ShokoUrlAction Action)[] ActionSuffixes =
    {
        ("/open-folder", ShokoUrlAction.OpenFolderRelative),
        ("/play", ShokoUrlAction.Play),
    };

    const string _fullPrefix = "shoko://";

    const string _shortPrefix = "shoko:";

    /// <summary>
    /// Parse a raw shoko:// URL into a structured result.
    /// Returns null if the URL is invalid or unknown.
    /// </summary>
    public static ParsedShokoUrl Parse(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return new() { Action = ShokoUrlAction.Unknown };

        // Strip "shoko://" or "shoko:" prefix (order matters — check longer form first)
        var url = rawUrl.Trim();
        if (url.StartsWith(_fullPrefix, StringComparison.OrdinalIgnoreCase))
            url = url[_fullPrefix.Length..];
        else if (url.StartsWith(_shortPrefix, StringComparison.OrdinalIgnoreCase))
            url = url[_shortPrefix.Length..];
        else
            return new() { Action = ShokoUrlAction.Unknown };

        // Determine protocol
        string? protocol = null;
        string hostAndPath;
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            protocol = "https";
            hostAndPath = url["https://".Length..];
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            protocol = "http";
            hostAndPath = url["http://".Length..];
        }
        else
        {
            hostAndPath = url;
        }

        // Split host from path+query
        var firstSlash = hostAndPath.IndexOf('/');
        if (firstSlash < 0)
        {
            Logger.Warn("URL missing path: {Url}", rawUrl);
            return new() { Action = ShokoUrlAction.Unknown };
        }

        var host = hostAndPath[..firstSlash];
        if (string.IsNullOrWhiteSpace(host))
        {
            Logger.Warn("URL has empty host: {Url}", rawUrl);
            return new() { Action = ShokoUrlAction.Unknown };
        }
        var pathAndQuery = hostAndPath[firstSlash..];

        // Split path and query at '?'
        var questionIdx = pathAndQuery.IndexOf('?');
        var path = questionIdx >= 0 ? pathAndQuery[..questionIdx] : pathAndQuery;
        var query = questionIdx >= 0 ? pathAndQuery[questionIdx..] : string.Empty;

        // Check path against known action suffixes (longest match first)
        foreach (var (suffix, action) in ActionSuffixes)
        {
            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Extract sub-path: everything before the matched suffix
            var subPath = path.Length > suffix.Length
                ? path[..^suffix.Length]
                : string.Empty;

            return action switch
            {
                ShokoUrlAction.Play => ParsePlayAction(host, protocol, subPath, query),
                ShokoUrlAction.OpenFolderRelative => ParseOpenFolderAction(host, protocol, subPath, query),
                _ => new() { Action = ShokoUrlAction.Unknown },
            };
        }

        Logger.Warn("No known action suffix found in path: {Path}", path);
        return new() { Action = ShokoUrlAction.Unknown };
    }

    private static ParsedShokoUrl ParsePlayAction(string host, string? protocol, string subPath, string query)
    {
        var playlistId = ExtractQueryParam(query, "playlist");
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            Logger.Warn("Play action missing playlist parameter");
            PlatformNotificationService.Instance.Show(
                "Playback Error",
                "The play URL is missing the required \"playlist\" parameter.",
                NotificationSeverity.Error);
            return new() { Action = ShokoUrlAction.Unknown };
        }

        var routeKey = subPath.Length > 0 ? $"{host}{subPath}" : host;
        var settings = SettingsProvider.Instance.Settings;

        // Look up existing connection by route key
        var connection = settings.GetConnectionByRouteKey(routeKey);

        bool useHttps;
        if (connection is not null)
        {
            // Known connection — use stored values and ignore any key in the URL
            var matchingRoute = connection.Routes.Find(r =>
                string.Equals(r.BaseUrl.TrimEnd('/'), routeKey.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            useHttps = matchingRoute?.UseHttps ?? false;

            Logger.Info("Found connection '{Name}' for route {RouteKey}", connection.Name, routeKey);
        }
        else
        {
            // New connection — probe protocol and store
            if (protocol is not null)
            {
                useHttps = protocol.Equals("https", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var probed = ProbeProtocol(host);
                useHttps = probed == "https";
            }

            connection = settings.GetOrCreateConnectionByRouteKey(routeKey, useHttps);

            SettingsProvider.Instance.Save();
            Logger.Info("Created new connection '{Name}' for route {RouteKey} (HTTPS={UseHttps})",
                connection.Name, routeKey, useHttps);
        }

        // Build full base URL including sub-path
        var fullUrl = $"{(useHttps ? "https" : "http")}://{routeKey}";

        return new ParsedShokoUrl
        {
            Action = ShokoUrlAction.Play,
            ServerBaseUrl = fullUrl,
            PlaylistId = playlistId,
        };
    }

    private static ParsedShokoUrl ParseOpenFolderAction(string host, string? protocol, string subPath, string query)
    {
        var resolvedProtocol = protocol ?? ProbeProtocol(host);
        var baseUrl = $"{resolvedProtocol}://{host}{subPath}";

        // Relative: has managedFolder param
        var managedIdStr = ExtractQueryParam(query, "managedFolder");
        if (int.TryParse(managedIdStr, out var managedId))
        {
            var relativePath = ExtractQueryParam(query, "relativePath");
            return new ParsedShokoUrl
            {
                Action = ShokoUrlAction.OpenFolderRelative,
                ServerBaseUrl = baseUrl,
                ManagedFolderId = managedId,
                RelativePath = relativePath
            };
        }

        // Absolute: has path param
        var absolutePath = ExtractQueryParam(query, "path");
        if (!string.IsNullOrWhiteSpace(absolutePath))
        {
            return new ParsedShokoUrl
            {
                Action = ShokoUrlAction.OpenFolderAbsolute,
                ServerBaseUrl = baseUrl,
                AbsolutePath = absolutePath
            };
        }

        Logger.Warn("Open-folder action missing required managedFolder or path parameter");
        PlatformNotificationService.Instance.Show(
            "Open Folder Error",
            "The open-folder URL is missing the required \"managedFolder\" or \"path\" parameter.",
            NotificationSeverity.Error);
        return new() { Action = ShokoUrlAction.Unknown };
    }

    /// <summary>
    /// Probe whether the host supports HTTPS by checking /api/v3/Init/Version.
    /// Returns "https" on success, "http" as fallback.
    /// </summary>
    private static string ProbeProtocol(string host)
    {
        var httpsUrl = $"https://{host}/api/v3/Init/Version";
        try
        {
            var response = ProbeHttp.GetAsync(httpsUrl)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();

            if (response.IsSuccessStatusCode)
            {
                var body = response.Content.ReadAsStringAsync()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                // Validate we got a JSON object back
                if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith("{"))
                {
                    Logger.Info("Protocol probe: HTTPS works for {Host}", host);
                    return "https";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "HTTPS probe failed for {Host}, falling back to HTTP", host);
        }

        Logger.Info("Protocol probe: falling back to HTTP for {Host}", host);
        return "http";
    }

    /// <summary>
    /// Extract a named query parameter from a query string.
    /// </summary>
    internal static string? ExtractQueryParam(string query, string paramName)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var parsed = HttpUtility.ParseQueryString(query);
        var val = parsed[paramName];
        return string.IsNullOrWhiteSpace(val) ? null : val;
    }
}
