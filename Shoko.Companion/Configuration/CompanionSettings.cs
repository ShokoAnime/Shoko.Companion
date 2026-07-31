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
    /// The effective Discord client ID — prefers a user override, falls back to the built-in default.
    /// </summary>
    [JsonIgnore]
    public string DiscordClientId => DiscordClientIdOverride ?? DefaultDiscordClientId;

    /// <summary>
    /// Whether Discord Rich Presence can be used given current settings.
    /// </summary>
    [JsonIgnore]
    public bool CanUseDiscord => DiscordEnabled && !string.IsNullOrWhiteSpace(DiscordClientId);

    // ── Privacy Mode ────────────────────────────────────────────────────

    /// <summary>
    /// Global master switch for privacy mode. When enabled, the sub-toggles
    /// below determine which features are restricted. When disabled, all
    /// sub-toggles are ignored and individual feature toggles control behavior.
    /// </summary>
    public bool PrivacyMode { get; set; }

    /// <summary>
    /// When privacy mode is active, hides the anime title and poster image
    /// from Discord presence. Shows generic "Watching Anime" instead.
    /// Replaces the old <c>DiscordPrivacyMode</c> setting.
    /// </summary>
    public bool PrivacyModeHideDiscord { get; set; }

    /// <summary>
    /// When privacy mode is active, strips identifying media information
    /// (title, stream URL, file ID, thumbnail) from the state reported to
    /// the Media Session hub. Basic playback state (Playing/Paused/Stopped)
    /// and position/duration are still reported.
    /// </summary>
    public bool PrivacyModeHideMediaPlaybackInfo { get; set; }

    /// <summary>
    /// When privacy mode is active, disallows remote clients from starting,
    /// pausing, resuming, seeking, or stopping playback via the Media
    /// Session API. Overrides <see cref="AllowRemotePlay"/>.
    /// </summary>
    public bool PrivacyModeDisableRemoteControl { get; set; }

    /// <summary>
    /// When privacy mode is active, disallows remote clients from capturing
    /// screenshots via the Media Session API.
    /// Overrides <see cref="AllowRemoteScreenshot"/>.
    /// </summary>
    public bool PrivacyModeDisableRemoteScreenshots { get; set; }

    /// <summary>
    /// When privacy mode is active, disables all playback event syncing
    /// (scrobbling) to the Shoko server.
    /// Overrides <see cref="PlaybackSyncingEnabled"/>.
    /// </summary>
    public bool PrivacyModeDisablePlaybackEvents { get; set; }

    /// <summary>
    /// The mpv keybinding used to toggle privacy mode during playback.
    /// Sent as a <c>keybind</c> command over JSON IPC when mpv connects.
    /// Format follows mpv's input.conf syntax (e.g. <c>Ctrl+p</c>).
    /// </summary>
    public string PrivacyModeMpvKeybinding { get; set; } = "Ctrl+p";

    /// <summary>
    /// The mpv keybinding used to open the companion settings window
    /// during playback. Sent as a <c>keybind</c> command over JSON IPC
    /// when mpv connects.
    /// </summary>
    public string SettingsMpvKeybinding { get; set; } = "Ctrl+Shift+o";

    /// <summary>
    /// Controls whether mpv subtitles are hidden before capturing a screenshot.
    /// <c>OnlyWhenPaused</c> (default) avoids visual flicker during active playback.
    /// </summary>
    public ScreenshotSubtitleBehavior ScreenshotSubtitleBehavior { get; set; } = ScreenshotSubtitleBehavior.OnlyWhenPaused;

    // ── Media Session API ───────────────────────────────────────────────

    /// <summary>
    /// The GUID of the server connection to auto-connect for the
    /// Media Session API. Null means no auto-connect.
    /// </summary>
    public Guid? MediaSessionAutoConnectId { get; set; }

    /// <summary>
    /// Allow remote clients to start new playback on this device.
    /// When false, <c>CanPlay</c> is reported as disabled.
    /// </summary>
    public bool AllowRemotePlay { get; set; } = true;

    /// <summary>
    /// Allow remote clients to request a screenshot of the current
    /// video frame. When false, <c>CanCaptureScreenshot</c> is reported
    /// as disabled.
    /// </summary>
    public bool AllowRemoteScreenshot { get; set; } = true;

    /// <summary>
    /// Master toggle for syncing playback events (start, end, pause, resume) to the Shoko server.
    /// When false, all syncing is disabled including live progress updates.
    /// </summary>
    public bool PlaybackSyncingEnabled { get; set; } = true;

    /// <summary>
    /// How aggressively to sync playback events to the Shoko server.
    /// <c>AfterPlayback</c>: stop event only.
    /// <c>OnEveryEvent</c>: play/pause/resume/stop events.
    /// <c>LiveSync</c> (default): play/pause/resume/stop + periodic live progress.
    /// Only effective when <see cref="PlaybackSyncingEnabled"/> is true.
    /// </summary>
    public PlaybackSyncingBehavior PlaybackSyncingBehavior { get; set; } = PlaybackSyncingBehavior.AfterPlayback;

    /// <summary>
    /// When true, launches mpv in full screen mode.
    /// </summary>
    public bool MpvFullScreen { get; set; } = true;

    /// <summary>
    /// When true, mpv starts playback in a paused state so the user
    /// can manually resume when ready.
    /// </summary>
    public bool MpvStartPaused { get; set; }

    /// <summary>
    /// When true, the volume level is persisted between sessions and restored
    /// on each playback start. When false, mpv's own volume configuration
    /// (mpv.conf / per-user config) is left untouched.
    /// </summary>
    public bool RestoreVolume { get; set; }

    /// <summary>
    /// The saved volume level (0–130), or null if never set. Only applied
    /// when <see cref="RestoreVolume"/> is true and this has a value.
    /// </summary>
    [Range(0, 130)]
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
    /// When privacy mode is active and <see cref="PrivacyModeForRestrictedContent"/>
    /// is also active, restricted content auto-triggers privacy mode using the
    /// configured sub-toggles. The transient <see cref="RestrictedContentPlaying"/>
    /// flag is managed by the session manager.
    /// </summary>
    [JsonIgnore]
    public bool EffectivePrivacyMode => PrivacyMode || (RestrictedContentPlaying && PrivacyModeForRestrictedContent);

    /// <summary>
    /// Transient flag set by the playback session manager. True when the current
    /// playback session contains restricted (adult) content.
    /// </summary>
    [JsonIgnore]
    public bool RestrictedContentPlaying { get; set; }

    /// <summary>
    /// When true, restricted content automatically enables privacy mode using
    /// the configured sub-toggles. The session manager sets
    /// <see cref="RestrictedContentPlaying"/> when restricted content plays.
    /// </summary>
    public bool PrivacyModeForRestrictedContent { get; set; }

    /// <summary>
    /// Global enabled switch for Media Session API integration.
    /// When disabled, the companion won't auto-connect and the settings
    /// section is hidden unless probing finds the plugin on a connection.
    /// </summary>
    public bool MediaSessionEnabled { get; set; }

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
