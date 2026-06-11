using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Newtonsoft.Json;

namespace Shoko.Companion.Configuration;

/// <summary>
/// Application settings persisted as JSON in the config directory.
/// </summary>
public class CompanionSettings
{
    /// <summary>
    /// Built-in Discord App ID for Rich Presence. Users can override in settings.
    /// </summary>
    internal const string DefaultDiscordClientId = "1511168896223150233";

    /// <summary>
    /// Whether any server connections are configured.
    /// </summary>
    [JsonIgnore]
    public bool HasConnections => Connections.Count > 0;

    private List<ServerConnection> _connections = [];

    /// <summary>
    /// Known server connections. Each holds its own display name, routes,
    /// API key, cached folder metadata, and per-connection preferences.
    /// </summary>
    public List<ServerConnection> Connections
    {
        get => _connections;
        set => _connections = value ?? [];
    }

    /// <summary>
    /// Controls what happens when a new shoko: URL arrives while something is playing.
    /// Replace = stop current + start new; Ignore = silently ignore; Append = add to playlist.
    /// </summary>
    public OnNewUrlBehavior OnNewUrlAction { get; set; } = OnNewUrlBehavior.Append;

    /// <summary>
    /// When true, always resolve the server URL through the connection's
    /// configured route table, bypassing the direct init-endpoint check
    /// on the URL-provided address. Useful when routing through reverse
    /// proxies, OAuth gateways, or when the URL-provided address is not
    /// directly reachable from this machine.
    /// When false (default), the URL-provided address is tried first and
    /// configured routes are used only as a fallback.
    /// </summary>
    public bool AlwaysUseConfiguredRoutes { get; set; }

    /// <summary>
    /// The last successfully resolved server base URL. Used by the "Open WebUI" tray action.
    /// Populated automatically by <see cref="Server.RouteResolver"/> on successful resolution.
    /// </summary>
    public string? LastUsedServerUrl { get; set; }

    /// <summary>
    /// Optional override path to the mpv executable.
    /// </summary>
    public string? MpvPath { get; set; }

    /// <summary>
    /// Optional override Discord application Client ID for Rich Presence.
    /// When set, this is used instead of the built-in default.
    /// </summary>
    public string? DiscordClientIdOverride { get; set; }

    /// <summary>
    /// Whether Discord Rich Presence integration is enabled.
    /// </summary>
    public bool DiscordEnabled { get; set; }

    /// <summary>
    /// Whether to show an idle/browsing presence when nothing is playing.
    /// </summary>
    public bool DiscordIdlePresence { get; set; }

    /// <summary>
    /// Source for Discord Rich Presence button 1. Defaults to AniDB.
    /// </summary>
    public Discord.DiscordButtonSource DiscordButton1 { get; set; } = Discord.DiscordButtonSource.AniDB;

    /// <summary>
    /// Source for Discord Rich Presence button 2. Defaults to disabled.
    /// </summary>
    public Discord.DiscordButtonSource DiscordButton2 { get; set; } = Discord.DiscordButtonSource.Disabled;

    /// <summary>
    /// When true, hides the anime title and poster image from Discord presence.
    /// Shows generic "Watching Anime" with episode info only.
    /// </summary>
    public bool DiscordPrivacyMode { get; set; }

    /// <summary>
    /// The effective Discord client ID — prefers a user override, falls back to the built-in default.
    /// </summary>
    [JsonIgnore]
    public string DiscordClientId => DiscordClientIdOverride ?? DefaultDiscordClientId;

    /// <summary>
    /// Whether Discord Rich Presence can be used given current settings.
    /// </summary>
    [JsonIgnore]
    public bool CanUseDiscord => DiscordEnabled && !string.IsNullOrWhiteSpace(DiscordClientId);

    /// <summary>
    /// Master toggle for syncing playback events (start, end, pause, resume) to the Shoko server.
    /// When false, all syncing is disabled including live progress updates.
    /// </summary>
    public bool PlaybackSyncingEnabled { get; set; } = true;

    /// <summary>
    /// When true, sends periodic position updates during playback.
    /// Requires <see cref="PlaybackSyncingEnabled"/> to be true to take effect.
    /// </summary>
    public bool LivePlaybackSyncingEnabled { get; set; }

    /// <summary>
    /// When true, launches mpv in full screen mode.
    /// </summary>
    public bool MpvFullScreen { get; set; } = true;

    /// <summary>
    /// When true, the volume level is persisted between sessions and restored
    /// on each playback start. When false, mpv's own volume configuration
    /// (mpv.conf / per-user config) is left untouched.
    /// </summary>
    public bool RestoreVolume { get; set; }

    /// <summary>
    /// The saved volume level (0–100), or null if never set. Only applied
    /// when <see cref="RestoreVolume"/> is true and this has a value.
    /// </summary>
    [Range(0, 100)]
    public int? Volume { get; set; }

    /// <summary>
    /// Number of initial non-pause playback events to skip after starting,
    /// to let the player settle before sending scrobbles.
    /// </summary>
    public int SyncUserDataInitialSkipEventCount { get; set; } = 3;

    /// <summary>
    /// Number of 10s timer ticks to accumulate before sending a live progress
    /// scrobble during smooth playback (no seeks). Higher values = fewer
    /// requests. Default 6 = one scrobble per 60 seconds.
    /// </summary>
    [Range(1, 60)]
    public int SyncUserDataLiveScrobbleTickThreshold { get; set; } = 6;

    /// <summary>
    /// When true, skips syncing for restricted (adult) content.
    /// </summary>
    public bool SkipRestrictedContent { get; set; } = true;

    /// <summary>
    /// NLog log level (Trace, Debug, Info, Warn, Error).
    /// </summary>
    public string LogLevel { get; set; } = "Info";

    /// <summary>
    /// True once we've asked the user about URL scheme registration.
    /// </summary>
    public bool UrlSchemeRegistrationAsked { get; set; }

    /// <summary>
    /// Find a connection whose routes contain the given route key
    /// (host:port[/subpath]), case-insensitive.
    /// Returns null if no match is found.
    /// </summary>
    public ServerConnection? GetConnectionByRouteKey(string routeKey)
    {
        if (string.IsNullOrWhiteSpace(routeKey))
            return null;

        var normalized = routeKey.TrimEnd('/');
        return Connections.FirstOrDefault(c =>
            c.Routes.Any(r =>
                string.Equals(r.BaseUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Find a connection by its route key, or create and add a new one.
    /// The new connection's name is inferred from the host:port portion of the route key.
    /// </summary>
    public ServerConnection GetOrCreateConnectionByRouteKey(string routeKey, bool useHttps)
    {
        var existing = GetConnectionByRouteKey(routeKey);
        if (existing is not null)
            return existing;

        // Infer name from host:port portion
        var hostPort = routeKey.Split('/')[0];
        var conn = new ServerConnection
        {
            Name = hostPort,
            Routes = [new ConnectionRoute { BaseUrl = routeKey.TrimEnd('/'), UseHttps = useHttps }]
        };
        Connections.Add(conn);
        return conn;
    }
}
