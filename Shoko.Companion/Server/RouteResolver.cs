using System;
using System.Net.Http;
using System.Threading.Tasks;
using NLog;
using Shoko.Companion.Configuration;

namespace Shoko.Companion.Server;

/// <summary>
/// Resolves the best reachable server base URL by trying the URL-provided
/// address first (without following redirects), then falling back to the
/// connection's configured route table with route probing.
///
/// Shared by both playback and open-folder flows to ensure they use the
/// same route resolution strategy.
/// </summary>
public static class RouteResolver
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Resolve the best reachable base URL for the given server address.
    /// </summary>
    /// <param name="serverBaseUrl">The base URL from the shoko:// URL (e.g. <c>http://myserver:8111</c>).</param>
    /// <returns>
    /// The resolved base URL, or <c>null</c> if neither direct reachability
    /// nor the connection route table yielded a result.
    /// </returns>
    /// <remarks>
    /// Resolution strategy:
    /// <list type="number">
    ///   <item>If <see cref="CompanionSettings.AlwaysUseConfiguredRoutes"/> is on → skip the direct check entirely, use connection routes.</item>
    ///   <item>Otherwise, try the URL-provided address directly via <c>/api/v3/Init/Version</c> (no redirect follow).</item>
    ///   <item>If that fails (3xx/4xx/5xx/error/non-JSON), look up the connection by route key and call <see cref="ServerConnection.ProbeReachableBaseUrl"/>.</item>
    /// </list>
    /// </remarks>
    public static async Task<string?> ResolveBestBaseUrlAsync(string serverBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(serverBaseUrl))
            return null;

        var settings = SettingsProvider.Instance.Settings;

        settings.LastUsedServerUrl = serverBaseUrl;
        SettingsProvider.Instance.Save();

        if (settings.AlwaysUseConfiguredRoutes)
        {
            Logger.Debug("AlwaysUseConfiguredRoutes is enabled — skipping direct check for {Url}", serverBaseUrl);
            return ResolveViaRouteTable(serverBaseUrl, settings);
        }

        // Try the URL-provided address directly (no redirect follow)
        if (await CheckDirectReachabilityAsync(serverBaseUrl))
        {
            Logger.Debug("Direct check succeeded for {Url}, using as-is", serverBaseUrl);
            return serverBaseUrl;
        }

        // Fall back to connection route table
        Logger.Debug("Direct check failed for {Url}, falling back to route table", serverBaseUrl);
        return ResolveViaRouteTable(serverBaseUrl, settings);
    }

    /// <summary>
    /// Look up the connection that matches the given server URL and probe its
    /// route table for the first reachable URL.
    /// </summary>
    private static string? ResolveViaRouteTable(string serverBaseUrl, CompanionSettings settings)
    {
        var routeKey = ExtractRouteKey(serverBaseUrl);
        if (routeKey is null)
        {
            Logger.Debug("Could not extract route key from {Url}", serverBaseUrl);
            return null;
        }

        var conn = settings.GetConnectionByRouteKey(routeKey);
        if (conn is null)
        {
            Logger.Debug("No configured connection found for route key {RouteKey}", routeKey);
            return null;
        }

        Logger.Debug("Probing connection '{Name}' route table for {Url}", conn.Name, serverBaseUrl);
        return conn.ProbeReachableBaseUrl();
    }

    /// <summary>
    /// Probe the Shoko init endpoint at the given base URL to verify it is
    /// directly reachable and returns a valid JSON response. Does NOT follow
    /// HTTP redirects so that reverse-proxy / OAuth redirects are detected
    /// as failures.
    /// </summary>
    private static async Task<bool> CheckDirectReachabilityAsync(string baseUrl)
    {
        try
        {
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/v3/Init/Version");

            if (!response.IsSuccessStatusCode)
            {
                Logger.Debug("Direct check for {BaseUrl} returned status {Status}",
                    baseUrl, (int)response.StatusCode);
                return false;
            }

            var body = await response.Content.ReadAsStringAsync();
            var isValid = !string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith("{");

            if (!isValid)
                Logger.Debug("Direct check for {BaseUrl} returned non-JSON body", baseUrl);

            return isValid;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Direct check for {BaseUrl} failed", baseUrl);
            return false;
        }
    }

    /// <summary>
    /// Strip the protocol from a full URL to get the route key (host:port[/subpath]).
    /// Returns null if the URL doesn't have a recognizable protocol.
    /// </summary>
    public static string? ExtractRouteKey(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var key = baseUrl;
        if (key.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            key = key["https://".Length..];
        else if (key.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            key = key["http://".Length..];
        else
            return null;

        return key.TrimEnd('/');
    }
}
