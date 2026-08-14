using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Discord;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Playback;

/// <summary>
/// Event arguments for a scrobble request emitted by <see cref="PlaybackSessionManager"/>.
/// </summary>
public class ScrobbleRequestEventArgs : EventArgs
{
    /// <summary>
    ///   The Shoko file ID to scrobble.
    /// </summary>
    public int FileId { get; init; }

    /// <summary>
    ///   The scrobble event type describing the playback event.
    /// </summary>
    public ScrobbleEventType EventType { get; init; }

    /// <summary>
    ///   Current playback position at the time of the event.
    /// </summary>
    public TimeSpan? Position { get; init; }

    /// <summary>
    ///   Whether the item should be marked as watched, or <c>null</c> to let
    ///   the server decide.
    /// </summary>
    public bool? IsWatched { get; init; }

    /// <summary>
    ///   Whether this event should also persist user data (stream selections).
    ///   Only true for "stop".
    /// </summary>
    public bool PersistUserData => EventType == ScrobbleEventType.PlaybackEnd;

    /// <summary>
    ///   Selected video track as a zero-based within-type ordinal, or
    ///   <c>null</c>. Not a container stream ID — see
    ///   <see cref="PlaybackSessionManager.SetVideoStream"/>.
    /// </summary>
    public int? VideoStreamOrdinal { get; init; }

    /// <summary>
    ///   Selected audio track as a zero-based within-type ordinal, or
    ///   <c>null</c>.
    /// </summary>
    public int? AudioStreamOrdinal { get; init; }

    /// <summary>
    ///   Selected subtitle track as a zero-based within-type ordinal, or
    ///   <c>null</c> when none is selected.
    /// </summary>
    public int? SubtitleStreamOrdinal { get; init; }
}

/// <summary>
/// Manages a single playback session: tracks position, pause state, metadata,
/// emits periodic scrobble and Discord presence events for the coordinator to handle.
/// </summary>
public class PlaybackSessionManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Raised when a scrobble should be sent to the Shoko server.</summary>
    public event EventHandler<ScrobbleRequestEventArgs>? ScrobbleRequested;

    /// <summary>
    ///   Raised on every scrobble timer tick (even when throttled), so the
    ///   media session hub gets periodic position updates during playback.
    ///   The argument is the current position as a <see cref="TimeSpan"/>.
    /// </summary>
    public event EventHandler<TimeSpan>? PositionTick;

    /// <summary>Raised when the Discord presence should be updated, or null to clear.</summary>
    public event EventHandler<DiscordPresenceData?>? DiscordPresenceChanged;

    private PlaybackSession? _session;
    private Timer? _scrobbleTimer;
    private const int ScrobbleIntervalMs = 10_000;

    private bool _mediaSessionConnected;

    /// <summary>
    ///   Whether a media session is registered with the Media Session plugin right
    ///   now — pushed here by <see cref="PlaybackCoordinator.MediaSessionId"/>,
    ///   which is set from the hub as the session registers, reconnects and
    ///   goes away.
    ///
    ///   <para>
    ///     Registering a session is consent to the server writing this
    ///     viewer's watch state, so while one is up the server is already
    ///     writing and anything this companion writes is a duplicate. Setting
    ///     this to <c>true</c> therefore <b>stands the companion's own
    ///     syncing down immediately</b>, mid-item and all: the server can
    ///     complete a record it took over part-way, because it has been
    ///     watching the same playback through the state reports since the
    ///     session registered.
    ///   </para>
    ///   <para>
    ///     Setting it back to <c>false</c> does <b>not</b> resume mid-item.
    ///     A watch record is about one whole viewing, and the item playing
    ///     when the session went away is the server's — most sharply at the
    ///     end, where <see cref="ScrobbleRequestEventArgs.PersistUserData"/>
    ///     fires on <see cref="ScrobbleEventType.PlaybackEnd"/> and a
    ///     companion that took over at 80% would mark the item watched on
    ///     the strength of the 20% it saw. Ownership is therefore latched per
    ///     item in <see cref="PlaybackSession.SyncSuppressed"/> and only
    ///     re-read at an item boundary — <see cref="StartSession"/> or
    ///     <see cref="OnNextFile"/>. The cost is deliberate: that item syncs
    ///     to whatever the server last wrote and no further.
    ///   </para>
    ///   <para>
    ///     "Gone" means gone as the hub client sees it, and that is
    ///     deliberately not a timeout of this class's own. The SignalR
    ///     connection retries forever and reclaims its session id through
    ///     <c>ReconnectSession</c>, so a socket that drops and comes back in
    ///     two seconds never clears the id and never reaches here — only
    ///     disposing the client (disconnecting, switching servers, quitting)
    ///     does.
    ///   </para>
    /// </summary>
    public bool MediaSessionConnected
    {
        get => _mediaSessionConnected;
        set
        {
            if (_mediaSessionConnected == value)
                return;

            _mediaSessionConnected = value;

            if (value)
            {
                if (_session is { SyncSuppressed: false })
                {
                    _session.SyncSuppressed = true;
                    Logger.Info(
                        "Media session connected — own syncing stands down now for file {File} at {Pos:F0}ms; the server owns this viewing",
                        _session.VideoId, _session.PositionMs);
                }
                else
                {
                    Logger.Info("Media session connected — own syncing is off for as long as it lasts");
                }

                // The hub is how the server sees this playback at all, and the
                // position reports it needs ride on the same timer as our own
                // scrobbling. Standing down must not silence them.
                EnsureScrobbleTimer();
            }
            else if (_session is not null)
            {
                Logger.Info(
                    "Media session gone — own syncing stays off for file {File}, which the server owned; it resumes on the next item",
                    _session.VideoId);
            }
            else
            {
                Logger.Info("Media session gone — own syncing resumes on the next item");
            }
        }
    }

    private static readonly Regex EpisodeTitlePattern = new(@"^Episode\s+\d+$", RegexOptions.IgnoreCase);

    private const double AutoWatchRatio = 0.975;

    /// <summary>Whether a playback session is currently active.</summary>
    public bool HasActiveSession => _session is not null;

    /// <summary>The file ID of the current session, or null.</summary>
    public int? CurrentVideoId => _session?.VideoId;

    /// <summary>
    ///   The selected video, audio and subtitle streams of the current
    ///   session as zero-based within-type ordinals, all null when there is
    ///   no session. See <see cref="SetVideoStream"/> for the convention.
    /// </summary>
    public (int? Video, int? Audio, int? Subtitle) CurrentStreamOrdinals
        => (_session?.VideoStreamOrdinal,
            _session?.AudioStreamOrdinal,
            _session?.SubtitleStreamOrdinal);

    /// <summary>The last known playback position in milliseconds.</summary>
    public double CurrentPositionMs => _session?.PositionMs ?? 0;

    /// <summary>Whether end-of-file was reached in the current session.</summary>
    public bool HasReachedEof => _session?.EofReached ?? false;

    /// <summary>
    /// Start tracking a new playback session.
    /// </summary>
    public void StartSession(int fileId, double resumePositionMs, double durationMs, bool isRestricted,
        string? seriesTitle, string? episodeTitle, int epNumber, int epNumberRange, int epCount,
        string? posterUrl, int animeId, PlaylistEpisodeIDsDto? episodeIds = null)
    {
        var settings = SettingsProvider.Instance.Settings;
        _session = new PlaybackSession
        {
            VideoId = fileId,
            PositionMs = resumePositionMs,
            InitialPositionMs = resumePositionMs,
            DurationMs = durationMs,
            IsRestricted = isRestricted,
            SeriesTitle = seriesTitle,
            EpisodeTitle = episodeTitle,
            EpisodeNumber = epNumber,
            EpisodeNumberRange = epNumberRange,
            EpisodeCount = epCount,
            PosterUrl = posterUrl,
            AnidbAnimeId = animeId,
            SkipEventCount = settings.SyncUserDataInitialSkipEventCount,
            TickThreshold = settings.SyncUserDataLiveScrobbleTickThreshold,
            TmdbShow = episodeIds?.TmdbShow,
            TmdbMovie = episodeIds?.TmdbMovie,
            TvdbShow = episodeIds?.TvdbShow,
            ImdbMovie = episodeIds?.ImdbMovie,
            // An item boundary is the one point where "who owns this record"
            // has a single answer, so it is the only point at which ownership
            // is re-read. See MediaSessionConnected.
            SyncSuppressed = _mediaSessionConnected,
        };

        Logger.Info(_mediaSessionConnected
            ? "File {File}: the server owns this viewing — own syncing off, a media session is connected"
            : "File {File}: this companion owns this viewing — no media session is connected", fileId);

        var ownSyncingAllowed = settings.PlaybackSyncingEnabled && !(settings.EffectivePrivacyMode && settings.PrivacyModeDisablePlaybackEvents);

        // The timer beats for two things — our own scrobbling, and the
        // position ticks the media session hub is fed from — so it runs while
        // either wants it. Standing down silences the scrobbles, never the
        // ticks.
        if (ownSyncingAllowed || _mediaSessionConnected)
        {
            _scrobbleTimer = new Timer(OnScrobbleTimer, null, ScrobbleIntervalMs, ScrobbleIntervalMs);
            Logger.Debug("Live scrobble timer started: interval={Interval}ms", ScrobbleIntervalMs);
        }

        SettingsProvider.Instance.Settings.RestrictedContentPlaying = isRestricted;
        EmitDiscordPresence();
    }

    /// <summary>
    /// Called on each time-pos change from mpv.
    /// </summary>
    public void OnPositionChanged(double positionMs)
    {
        if (_session is null) return;
        _session.PositionMs = positionMs;
    }

    /// <summary>
    /// Called when mpv performs a seek. Syncs position and triggers a scrobble
    /// check immediately so the sync logic can evaluate the new position.
    /// </summary>
    public void OnSeek(double positionMs)
    {
        if (_session is null)
            return;

        Logger.Info("Seek detected — new position {Pos:F0}ms", positionMs);

        _session.PositionMs = positionMs;
        if (!_session.IsPaused)
            _scrobbleTimer?.Change(ScrobbleIntervalMs, ScrobbleIntervalMs);

        var pos = TimeSpan.FromMilliseconds(positionMs);
        Task.Run(() => PositionTick?.Invoke(this, pos));
    }

    /// <summary>
    /// Called when mpv pause property changes.
    /// </summary>
    public void OnPauseChanged(bool isPaused)
    {
        if (_session is null) return;

        var wasPaused = _session.IsPaused;
        _session.IsPaused = isPaused;

        if (isPaused && !wasPaused)
        {
            Logger.Info("Session paused at {Pos:F0}ms (dur={Dur:F0}ms)", _session.PositionMs, _session.DurationMs);

            _scrobbleTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            if (ShouldSendEvent(isPauseOrResume: true))
                EmitPlaybackEvent(ScrobbleEventType.PlaybackPause, _session.PositionMs, watched: null);
        }
        else if (!isPaused && wasPaused)
        {
            Logger.Info("Session resumed at {Pos:F0}ms", _session.PositionMs);

            _scrobbleTimer?.Change(ScrobbleIntervalMs, ScrobbleIntervalMs);
            _session.ScrobbleTickCount = 0;

            if (ShouldSendEvent(isPauseOrResume: true))
                EmitPlaybackEvent(ScrobbleEventType.PlaybackResume, _session.PositionMs, watched: null);
        }

        EmitDiscordPresence();
    }

    /// <summary>
    /// Called when mpv hits end-of-file naturally.
    /// </summary>
    public void OnEofReached()
    {
        _session?.EofReached = true;
    }

    /// <summary>
    ///   Set the selected video track (null = none).
    ///
    ///   The value is the <b>within-type ordinal, zero-based</b>: filter
    ///   the file's streams to the kind and count from there. That is the
    ///   whole of the cross-client interop for Shoko's single stored
    ///   integer per kind — a client that persists a container stream ID
    ///   instead is self-consistent on its own and sends every other
    ///   client to the wrong track.
    /// </summary>
    public void SetVideoStream(int? ordinal)
    {
        _session?.VideoStreamOrdinal = ordinal;
    }

    /// <summary>
    ///   Set the selected audio track as a zero-based within-type ordinal
    ///   (null = none). See <see cref="SetVideoStream"/> for the
    ///   convention.
    /// </summary>
    public void SetAudioStream(int? ordinal)
    {
        _session?.AudioStreamOrdinal = ordinal;
    }

    /// <summary>
    ///   Set the selected subtitle track as a zero-based within-type
    ///   ordinal (null = none/disabled). See
    ///   <see cref="SetVideoStream"/> for the convention.
    /// </summary>
    public void SetSubtitleStream(int? ordinal)
    {
        _session?.SubtitleStreamOrdinal = ordinal;
    }

    /// <summary>
    /// End the current session, emitting a final stop scrobble.
    /// Returns the file ID that was being tracked, or null if no session.
    /// </summary>
    public int? EndSession(double finalPositionMs)
    {
        _scrobbleTimer?.Dispose();
        _scrobbleTimer = null;

        if (_session is null) return null;

        _session.PositionMs = finalPositionMs;

        // Determine watched: either EOF was hit, or position >= 97.5% of duration
        bool? watched = null;
        if (_session.EofReached)
        {
            watched = true;
        }
        else if (_session.DurationMs > 0 && finalPositionMs > 0 &&
                 (finalPositionMs / _session.DurationMs) >= AutoWatchRatio)
        {
            watched = true;
        }

        var fileId = _session.VideoId;
        var position = _session.PositionMs;
        var isRestricted = _session.IsRestricted;
        var videoStreamOrdinal = _session.VideoStreamOrdinal;
        var audioStreamOrdinal = _session.AudioStreamOrdinal;
        var subtitleStreamOrdinal = _session.SubtitleStreamOrdinal;
        var shouldSendStop = ShouldSendEvent(isPauseOrResume: true);
        var syncSuppressed = _session.SyncSuppressed;

        Logger.Info("Session ended at {Pos:F0}ms (dur={Dur:F0}ms, watched={Watched}, eof={Eof}, sendStop={SendStop}, serverOwned={Suppressed})",
            position, _session?.DurationMs ?? 0, watched, _session?.EofReached, shouldSendStop, syncSuppressed);
        SettingsProvider.Instance.Settings.RestrictedContentPlaying = false;
        _session = null;

        if (syncSuppressed)
            Logger.Info("File {File}: no stop scrobble — the server owned this viewing", fileId);

        var settings = SettingsProvider.Instance.Settings;
        if (shouldSendStop && !syncSuppressed && settings.PlaybackSyncingEnabled && !(settings.EffectivePrivacyMode && settings.PrivacyModeDisablePlaybackEvents))
        {
            Task.Run(() => ScrobbleRequested?.Invoke(this, new ScrobbleRequestEventArgs
            {
                FileId = fileId,
                EventType = ScrobbleEventType.PlaybackEnd,
                Position = position > 0 ? TimeSpan.FromMilliseconds(position) : null,
                IsWatched = watched,
                VideoStreamOrdinal = videoStreamOrdinal,
                AudioStreamOrdinal = audioStreamOrdinal,
                SubtitleStreamOrdinal = subtitleStreamOrdinal,
            }));
        }

        DiscordPresenceChanged?.Invoke(this, null);

        return fileId;
    }

    /// <summary>
    /// Called when playback reaches the end of a file in a multi-item playlist
    /// (end-file with reason "eof" and more items pending). Emits a <see cref="ScrobbleEventType.PlaybackEnd"/>
    /// event for the current file so it gets scrobbled before the session moves on,
    /// without ending the session itself.
    /// </summary>
    public void FinalizeCurrentFile()
    {
        if (_session is null)
            return;

        if (_session.SyncSuppressed)
        {
            Logger.Info("File {File}: not finalized by us — the server owned this viewing", _session.VideoId);
            return;
        }

        var settings = SettingsProvider.Instance.Settings;
        if (!settings.PlaybackSyncingEnabled || (settings.EffectivePrivacyMode && settings.PrivacyModeDisablePlaybackEvents))
            return;

        // Determine watched: either EOF was hit, or position >= 97.5% of duration
        bool? watched = null;
        if (_session.EofReached)
        {
            watched = true;
        }
        else if (_session.DurationMs > 0 && _session.PositionMs > 0 &&
                 (_session.PositionMs / _session.DurationMs) >= AutoWatchRatio)
        {
            watched = true;
        }

        var fileId = _session.VideoId;
        var position = _session.PositionMs;
        var isRestricted = _session.IsRestricted;
        var videoStreamOrdinal = _session.VideoStreamOrdinal;
        var audioStreamOrdinal = _session.AudioStreamOrdinal;
        var subtitleStreamOrdinal = _session.SubtitleStreamOrdinal;
        var shouldSendStop = ShouldSendEvent(isPauseOrResume: true);

        Logger.Info("Finalizing file {File}: pos={Pos:F0}ms, dur={Dur:F0}ms, eof={Eof}, watched={Watched}, sendStop={SendStop}",
            fileId, position, _session.DurationMs, _session.EofReached, watched, shouldSendStop);

        if (!shouldSendStop)
            return;

        Task.Run(() => ScrobbleRequested?.Invoke(this, new ScrobbleRequestEventArgs
        {
            FileId = fileId,
            EventType = ScrobbleEventType.PlaybackEnd,
            Position = position > 0 ? TimeSpan.FromMilliseconds(position) : null,
            IsWatched = watched,
            VideoStreamOrdinal = videoStreamOrdinal,
            AudioStreamOrdinal = audioStreamOrdinal,
            SubtitleStreamOrdinal = subtitleStreamOrdinal,
        }));
    }

    /// <summary>
    /// Called when the playlist advances to the next file (end-file with non-eof reason).
    /// Resets scrobble throttling state without ending the session.
    /// </summary>
    public void OnNextFile(int fileId, double resumePositionMs, double durationMs, bool isRestricted,
        string? seriesTitle, string? episodeTitle, int epNumber, int epNumberRange, int epCount,
        string? posterUrl, int animeId, PlaylistEpisodeIDsDto? episodeIds = null)
    {
        if (_session is null) return;

        _session.VideoId = fileId;
        _session.PositionMs = resumePositionMs;
        _session.InitialPositionMs = resumePositionMs;
        _session.DurationMs = durationMs;
        _session.IsRestricted = isRestricted;
        SettingsProvider.Instance.Settings.RestrictedContentPlaying = isRestricted;
        _session.SeriesTitle = seriesTitle;
        _session.EpisodeTitle = episodeTitle;
        _session.EpisodeNumber = epNumber;
        _session.EpisodeNumberRange = epNumberRange;
        _session.EpisodeCount = epCount;
        _session.PosterUrl = posterUrl;
        _session.SentStartEvent = false;
        _session.SkipEventCount = SettingsProvider.Instance.Settings.SyncUserDataInitialSkipEventCount;
        _session.IsPaused = true;
        _session.EofReached = false;
        _session.VideoStreamOrdinal = null;
        _session.AudioStreamOrdinal = null;
        _session.SubtitleStreamOrdinal = null;
        _session.AnidbAnimeId = animeId;
        _session.TmdbShow = episodeIds?.TmdbShow;
        _session.TmdbMovie = episodeIds?.TmdbMovie;
        _session.TvdbShow = episodeIds?.TvdbShow;
        _session.ImdbMovie = episodeIds?.ImdbMovie;
        _session.ScrobbleTickCount = 0;
        _session.TickThreshold = SettingsProvider.Instance.Settings.SyncUserDataLiveScrobbleTickThreshold;

        // An item boundary — the one point where ownership of a watch record
        // has a single answer, and so the only point at which it is re-read.
        // See MediaSessionConnected.
        var wasSuppressed = _session.SyncSuppressed;
        _session.SyncSuppressed = _mediaSessionConnected;
        if (wasSuppressed && !_mediaSessionConnected)
            Logger.Info("File {File}: own syncing resumes here — the media session is gone and this is a new item", fileId);
        else if (!wasSuppressed && _mediaSessionConnected)
            Logger.Info("File {File}: the server owns this viewing — own syncing off, a media session is connected", fileId);

        EmitDiscordPresence();
    }

    private void OnScrobbleTimer(object? state)
    {
        // Fire position tick on every timer beat so the media session hub
        // gets regular position updates even when the scrobble is throttled.
        var pos = TimeSpan.FromMilliseconds(_session?.PositionMs ?? 0);
        Task.Run(() => PositionTick?.Invoke(this, pos));

        if (_session is null || _session.IsPaused)
            return;

        // Throttle: only scrobble every N ticks
        if (++_session.ScrobbleTickCount < _session.TickThreshold)
        {
            Logger.Trace("Scrobble timer tick throttled — tick {Tick}/{Threshold}", _session.ScrobbleTickCount, _session.TickThreshold);
            return;
        }


        Logger.Debug("Scrobble timer tick — emitting progress at {Pos:F0}ms", _session.PositionMs);
        _session.ScrobbleTickCount = 0;

        // Gate: skip count not exhausted yet
        if (!ShouldSendEvent())
        {
            Logger.Trace("Scrobble timer tick skipped — initial skip count not exhausted");
            return;
        }

        if (!_session.SentStartEvent)
        {
            _session.SentStartEvent = true;
            // Only send start event when behavior is LiveSync or OnEveryEvent
            if (SettingsProvider.Instance.Settings.PlaybackSyncingBehavior is PlaybackSyncingBehavior.LiveSync or PlaybackSyncingBehavior.OnEveryEvent)
                EmitPlaybackEvent(ScrobbleEventType.PlaybackStart, _session.InitialPositionMs, watched: null);
        }

        // Only send live progress when behavior is LiveSync
        if (SettingsProvider.Instance.Settings.PlaybackSyncingBehavior is PlaybackSyncingBehavior.LiveSync)
            EmitPlaybackEvent(ScrobbleEventType.PlaybackProgress, _session.PositionMs, watched: null);
    }

    /// <summary>
    ///   Start the tick timer if a session is running without one. The timer
    ///   is normally started by <see cref="StartSession"/>, which skips it
    ///   when nothing wants it; a media session connecting mid-item is the
    ///   case where something starts wanting it later, because the hub's
    ///   position reports ride on the same beat.
    /// </summary>
    private void EnsureScrobbleTimer()
    {
        if (_session is null || _scrobbleTimer is not null)
            return;

        _scrobbleTimer = new Timer(OnScrobbleTimer, null, ScrobbleIntervalMs, ScrobbleIntervalMs);
        Logger.Debug("Live scrobble timer started: interval={Interval}ms", ScrobbleIntervalMs);
    }

    private bool ShouldSendEvent(bool isPauseOrResume = false)
    {
        if (_session is null) return false;

        if (_session.SkipEventCount <= 0)
            return true;

        if (!isPauseOrResume)
            _session.SkipEventCount--;

        return _session.SkipEventCount <= 0;
    }

    private void EmitPlaybackEvent(ScrobbleEventType eventType, double position, bool? watched)
    {
        if (_session is null)
            return;

        // The server is writing this item's watch state, so anything we write
        // is a second writer on one record. See MediaSessionConnected.
        if (_session.SyncSuppressed)
        {
            Logger.Trace("Scrobble {Event} suppressed for file {File} — the server owns this viewing",
                eventType, _session.VideoId);
            return;
        }

        var settings = SettingsProvider.Instance.Settings;
        if (!settings.PlaybackSyncingEnabled)
            return;

        if (settings.EffectivePrivacyMode && settings.PrivacyModeDisablePlaybackEvents)
            return;

        // AfterPlayback only sends stop events (which bypass EmitPlaybackEvent)
        if (settings.PlaybackSyncingBehavior is not (PlaybackSyncingBehavior.OnEveryEvent or PlaybackSyncingBehavior.LiveSync))
            return;

        Task.Run(() => ScrobbleRequested?.Invoke(this, new ScrobbleRequestEventArgs
        {
            FileId = _session.VideoId,
            EventType = eventType,
            Position = position > 0 ? TimeSpan.FromMilliseconds(position) : null,
            IsWatched = watched
        }));
    }

    private void EmitDiscordPresence()
    {
        if (_session is null)
        {
            Task.Run(() => DiscordPresenceChanged?.Invoke(this, null));
            return;
        }

        var settings = SettingsProvider.Instance.Settings;
        var privacy = settings.EffectivePrivacyMode && settings.PrivacyModeHideDiscord;
        var buttons = BuildButtons(settings);

        if (privacy)
        {
            Task.Run(() => DiscordPresenceChanged?.Invoke(this, new DiscordPresenceData(
                Details: "Watching Anime",
                State: null,
                LargeImageKey: null,
                LargeImageText: null,
                StartTimeStamp: _session.IsPaused ? null : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Buttons: buttons
            )));
            return;
        }

        var showTitle = _session.EpisodeTitle is not null &&
            !EpisodeTitlePattern.IsMatch(_session.EpisodeTitle);

        var epRange = _session.EpisodeCount > 0
            ? _session.EpisodeNumberRange > 1
                ? $"{_session.EpisodeNumber}-{_session.EpisodeNumber + _session.EpisodeNumberRange - 1} of {_session.EpisodeCount}"
                : $"{_session.EpisodeNumber} of {_session.EpisodeCount}"
            : null;

        var state = showTitle && epRange is not null
            ? $"{_session.EpisodeTitle} — Episode {epRange}"
            : !showTitle && epRange is not null
                ? $"Episode {epRange}"
                : showTitle
                    ? _session.EpisodeTitle
                    : null;

        Task.Run(() => DiscordPresenceChanged?.Invoke(this, new DiscordPresenceData(
            Details: _session.SeriesTitle is not null ? $"Watching {_session.SeriesTitle}" : "Watching Anime",
            State: state,
            LargeImageKey: _session.PosterUrl ?? "shoko_default",
            LargeImageText: _session.SeriesTitle ?? "Shoko Server",
            StartTimeStamp: _session.IsPaused ? null : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Buttons: buttons
        )));
    }

    private IReadOnlyList<DiscordButtonData>? BuildButtons(CompanionSettings settings)
    {
        if (_session is null) return null;

        var list = new List<DiscordButtonData>(2);

        AddButton(list, settings.DiscordButton1);
        AddButton(list, settings.DiscordButton2);

        return list.Count > 0 ? list : null;
    }

    private void AddButton(List<DiscordButtonData> list, DiscordButtonSource source)
    {
        if (_session is null) return;

        switch (source)
        {
            case DiscordButtonSource.AniDB when _session.AnidbAnimeId > 0:
                list.Add(new DiscordButtonData("AniDB", $"https://anidb.net/anime/{_session.AnidbAnimeId}"));
                break;

            case DiscordButtonSource.TMDB:
                if (_session.TmdbShow is > 0)
                    list.Add(new DiscordButtonData("TMDB", $"https://www.themoviedb.org/tv/{_session.TmdbShow}"));
                else if (_session.TmdbMovie is > 0)
                    list.Add(new DiscordButtonData("TMDB", $"https://www.themoviedb.org/movie/{_session.TmdbMovie}"));
                break;

            case DiscordButtonSource.TvdbOrImdb:
                if (!string.IsNullOrWhiteSpace(_session.ImdbMovie))
                    list.Add(new DiscordButtonData("IMDb", $"https://www.imdb.com/title/{_session.ImdbMovie}"));
                else if (_session.TvdbShow is > 0)
                    list.Add(new DiscordButtonData("TVDB", $"https://www.thetvdb.com/series/{_session.TvdbShow}"));
                break;
        }
    }

    private class PlaybackSession
    {
        public int VideoId;
        public double PositionMs;
        public double InitialPositionMs;
        public double DurationMs;
        public bool IsPaused;
        public bool IsRestricted;
        public bool EofReached;

        public string? SeriesTitle;
        public string? EpisodeTitle;
        public int EpisodeNumber;
        public int EpisodeNumberRange;
        public int EpisodeCount;
        public string? PosterUrl;

        public bool SentStartEvent;
        public int SkipEventCount;

        public int? VideoStreamOrdinal;
        public int? AudioStreamOrdinal;
        public int? SubtitleStreamOrdinal;

        public int AnidbAnimeId;
        public int? TmdbShow;
        public int? TmdbMovie;
        public int? TvdbShow;
        public string? ImdbMovie;

        public int ScrobbleTickCount;
        public int TickThreshold;

        /// <summary>
        ///   True when the server owns this item's watch record and this
        ///   companion writes none of it. Latched at the item boundary from
        ///   <see cref="PlaybackSessionManager.MediaSessionConnected"/>, and
        ///   raised — never lowered — part-way through.
        /// </summary>
        public bool SyncSuppressed;
    }
}
