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
    ///   Selected video stream container ID, or <c>null</c>.
    /// </summary>
    public int? VideoStreamId { get; init; }

    /// <summary>
    ///   Selected audio stream container ID, or <c>null</c>.
    /// </summary>
    public int? AudioStreamId { get; init; }

    /// <summary>
    ///   Selected subtitle stream container ID, or <c>null</c>.
    /// </summary>
    public int? SubtitleStreamId { get; init; }
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

    /// <summary>Raised when the Discord presence should be updated, or null to clear.</summary>
    public event EventHandler<DiscordPresenceData?>? DiscordPresenceChanged;

    private PlaybackSession? _session;
    private Timer? _scrobbleTimer;
    private const int ScrobbleIntervalMs = 10_000;

    private static readonly Regex EpisodeTitlePattern = new(@"^Episode\s+\d+$", RegexOptions.IgnoreCase);

    private const double AutoWatchRatio = 0.975;

    /// <summary>Whether a playback session is currently active.</summary>
    public bool HasActiveSession => _session is not null;

    /// <summary>The file ID of the current session, or null.</summary>
    public int? CurrentFileId => _session?.FileId;

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
            FileId = fileId,
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
        };

        if (settings.LivePlaybackSyncingEnabled && settings.PlaybackSyncingEnabled)
        {
            _scrobbleTimer = new Timer(OnScrobbleTimer, null, ScrobbleIntervalMs, ScrobbleIntervalMs);
            Logger.Debug("Live scrobble timer started: interval={Interval}ms", ScrobbleIntervalMs);
        }

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

    /// <summary>Set the selected video stream container ID (null = none).</summary>
    public void SetVideoStream(int? streamId)
    {
        _session?.VideoStreamId = streamId;
    }

    /// <summary>Set the selected audio stream container ID (null = none).</summary>
    public void SetAudioStream(int? streamId)
    {
        _session?.AudioStreamId = streamId;
    }

    /// <summary>Set the selected subtitle stream container ID (null = none/disabled).</summary>
    public void SetSubtitleStream(int? streamId)
    {
        _session?.SubtitleStreamId = streamId;
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

        var fileId = _session.FileId;
        var position = _session.PositionMs;
        var isRestricted = _session.IsRestricted;
        var videoStreamId = _session.VideoStreamId;
        var audioStreamId = _session.AudioStreamId;
        var subtitleStreamId = _session.SubtitleStreamId;
        var shouldSendStop = ShouldSendEvent(isPauseOrResume: true);

        Logger.Info("Session ended at {Pos:F0}ms (dur={Dur:F0}ms, watched={Watched}, eof={Eof}, sendStop={SendStop})",
            position, _session?.DurationMs ?? 0, watched, _session?.EofReached, shouldSendStop);
        _session = null;

        if (shouldSendStop && SettingsProvider.Instance.Settings.PlaybackSyncingEnabled)
        {
            if (!isRestricted || !SettingsProvider.Instance.Settings.SkipRestrictedContent)
            {
                Task.Run(() => ScrobbleRequested?.Invoke(this, new ScrobbleRequestEventArgs
                {
                    FileId = fileId,
                    EventType = ScrobbleEventType.PlaybackEnd,
                    Position = position > 0 ? TimeSpan.FromMilliseconds(position) : null,
                    IsWatched = watched,
                    VideoStreamId = videoStreamId,
                    AudioStreamId = audioStreamId,
                    SubtitleStreamId = subtitleStreamId
                }));
            }
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

        if (!SettingsProvider.Instance.Settings.PlaybackSyncingEnabled)
            return;

        if (_session.IsRestricted && SettingsProvider.Instance.Settings.SkipRestrictedContent)
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

        var fileId = _session.FileId;
        var position = _session.PositionMs;

        Logger.Info("Finalizing file {File}: pos={Pos:F0}ms, dur={Dur:F0}ms, eof={Eof}, watched={Watched}",
            fileId, position, _session.DurationMs, _session.EofReached, watched);

        Task.Run(() => ScrobbleRequested?.Invoke(this, new ScrobbleRequestEventArgs
        {
            FileId = fileId,
            EventType = ScrobbleEventType.PlaybackEnd,
            Position = position > 0 ? TimeSpan.FromMilliseconds(position) : null,
            IsWatched = watched,
            VideoStreamId = _session.VideoStreamId,
            AudioStreamId = _session.AudioStreamId,
            SubtitleStreamId = _session.SubtitleStreamId,
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

        _session.FileId = fileId;
        _session.PositionMs = resumePositionMs;
        _session.InitialPositionMs = resumePositionMs;
        _session.DurationMs = durationMs;
        _session.IsRestricted = isRestricted;
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
        _session.VideoStreamId = null;
        _session.AudioStreamId = null;
        _session.SubtitleStreamId = null;
        _session.AnidbAnimeId = animeId;
        _session.TmdbShow = episodeIds?.TmdbShow;
        _session.TmdbMovie = episodeIds?.TmdbMovie;
        _session.TvdbShow = episodeIds?.TvdbShow;
        _session.ImdbMovie = episodeIds?.ImdbMovie;
        _session.ScrobbleTickCount = 0;
        _session.TickThreshold = SettingsProvider.Instance.Settings.SyncUserDataLiveScrobbleTickThreshold;

        EmitDiscordPresence();
    }

    private void OnScrobbleTimer(object? state)
    {
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
            EmitPlaybackEvent(ScrobbleEventType.PlaybackStart, _session.InitialPositionMs, watched: null);
        }

        EmitPlaybackEvent(ScrobbleEventType.PlaybackProgress, _session.PositionMs, watched: null);
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

        if (!SettingsProvider.Instance.Settings.PlaybackSyncingEnabled)
            return;

        if (_session.IsRestricted && SettingsProvider.Instance.Settings.SkipRestrictedContent)
            return;

        Task.Run(() => ScrobbleRequested?.Invoke(this, new ScrobbleRequestEventArgs
        {
            FileId = _session.FileId,
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
        var privacy = settings.DiscordPrivacyMode;
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
        public int FileId;
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

        public int? VideoStreamId;
        public int? AudioStreamId;
        public int? SubtitleStreamId;

        public int AnidbAnimeId;
        public int? TmdbShow;
        public int? TmdbMovie;
        public int? TvdbShow;
        public string? ImdbMovie;

        public int ScrobbleTickCount;
        public int TickThreshold;
    }
}
