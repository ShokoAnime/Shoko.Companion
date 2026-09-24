using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Newtonsoft.Json;
using Shoko.Companion.Playback;

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
    /// below determine which features are restricted locally. When disabled,
    /// all sub-toggles are ignored and individual feature toggles control
    /// behavior.
    /// <para>
    /// It is also the value sent to the media session plugin as
    /// <c>PrivacyModeEnabled</c>, which is what makes the server hide the
    /// items this session plays from other viewers and stop recording them.
    /// Sent as this raw value rather than as
    /// <see cref="EffectivePrivacyMode"/>: the server's master switch
    /// re-resolves the whole queue when it turns on, while its
    /// restricted-content rule deliberately never reaches back, so the two
    /// halves of the effective value travel on separate fields.
    /// </para>
    /// </summary>
    public bool PrivacyMode { get; set; }

    /// <summary>
    /// When privacy mode is active, hides the anime title and poster image
    /// from Discord presence. Shows generic "Watching Anime" instead.
    /// Replaces the old <c>DiscordPrivacyMode</c> setting.
    /// </summary>
    public bool PrivacyModeHideDiscord { get; set; }

    // ── Two settings used to sit here, and the server does both jobs ──
    //
    // `PrivacyModeHideMediaPlaybackInfo` stripped the title and file id
    // from the state reported to the media session hub, and
    // `PrivacyModeDisableRemoteControl` withheld thirteen capability flags.
    // Both are gone rather than deprecated, because the plugin now
    // resolves privacy per item from the declaration this client sends:
    // it withholds a private item from every observer as two opaque
    // fields — stricter than blanking a title, and it cannot forget a
    // field added next year — and it refuses remote control of one at the
    // session manager across eleven commands.
    //
    // Keeping local copies would mean two implementations of one rule,
    // and the interesting failure is not that they disagree but that only
    // one of them ratchets. Server-side, privacy never comes off an item
    // once it is on; a client-side mask is only ever as good as the
    // current value of a checkbox.
    //
    // Hiding information on the way *out* was also the wrong direction.
    // The client never lies to the server: it reports what it is playing
    // exactly as it always did, which is what keeps the playlist, handoff
    // and track selection working for the session itself, and the
    // filtering happens where the server speaks to anybody else.
    //
    // Old settings.json files carrying either key still load — an unknown
    // property is ignored — and the key is dropped on the next save.

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
    /// <para>
    /// Also sent to the media session plugin as
    /// <c>DisablePlaybackEventSyncing</c>, and both halves are needed.
    /// The two writers take turns: while a media session is registered the
    /// companion's own scrobbler stands down and the plugin writes the
    /// watch state instead. Told only locally, this switch would hold only
    /// while the hub was down and quietly stop meaning anything once it
    /// came up.
    /// </para>
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
    /// Allow remote clients to change volume and mute state on this device.
    /// When false, <c>CanSetVolume</c> is reported as disabled.
    /// </summary>
    public bool AllowRemoteVolumeControl { get; set; } = true;

    /// <summary>
    /// Allow another session to move what it is playing to this device,
    /// at its position and with its track selection. When false,
    /// <c>CanReceiveHandoff</c> is reported as disabled and the server
    /// refuses handoffs aimed here.
    /// <para>
    /// Defaults to <c>true</c>, unlike the browser player: this is a
    /// desktop player somebody is sitting at, so a video arriving from
    /// their phone is the point of the feature rather than a surprise.
    /// The privacy and remote-play switches still override it.
    /// </para>
    /// </summary>
    public bool AllowSessionHandoff { get; set; } = true;

    /// <summary>
    /// Master toggle for syncing playback events (start, end, pause, resume) to the Shoko server.
    /// When false, all syncing is disabled including live progress updates.
    /// <para>
    ///   Governs syncing <b>this companion does itself</b>, which is only
    ///   what plays while no media session is connected. Registering a
    ///   session with the media session plugin is consent to the server writing
    ///   watch state from the session's own state reports, so anything
    ///   played through one is synced by the server whatever this says:
    ///   leaving it on adds no second writer, and turning it off does not
    ///   stop the server writing. See
    ///   <see cref="Playback.PlaybackSessionManager.MediaSessionConnected"/>.
    /// </para>
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
    /// When true, mpv starts playback in a paused state so the user
    /// can manually resume when ready.
    /// </summary>
    public bool MpvStartPaused { get; set; }

    /// <summary>
    /// The saved fullscreen state, or false if never set. This is the
    /// source of truth for fullscreen: always restored when mpv connects,
    /// and always persisted when the fullscreen state changes (even while
    /// mpv is not running, so the value applies on the next play).
    /// </summary>
    public bool IsFullscreen { get; set; } = true;

    /// <summary>
    /// The saved volume level (0–130), or null if never set. This is the
    /// source of truth for volume: always restored when a file loads, and
    /// always persisted when the volume changes (even while mpv is not
    /// running, so the value applies on the next play).
    /// </summary>
    [Range(0, PlaybackCoordinator.MaxMpvVolume)]
    public int Volume { get; set; } = 100;

    /// <summary>
    /// The saved mute state, or false if never set. This is the source of
    /// truth for mute: always restored when a file loads, and always
    /// persisted when the mute state changes.
    /// </summary>
    public bool Muted { get; set; }

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
    /// <para>
    /// Also sent to the media session plugin as
    /// <c>AlwaysUsePrivacyModeForRestrictedContent</c>, where restricted is
    /// a server ruling rather than a client claim — any series the video
    /// belongs to having it set is enough — and it applies per item, to
    /// items added after it and never backwards into the queue.
    /// </para>
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
