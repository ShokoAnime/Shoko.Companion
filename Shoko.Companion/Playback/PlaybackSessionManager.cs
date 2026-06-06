using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Shoko.Companion.Configuration;
using Shoko.Companion.Discord;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Playback;

/// <summary>
/// Event arguments for a scrobble request emitted by <see cref="PlaybackSessionManager"/>.
/// </summary>
public class ScrobbleRequestEventArgs : EventArgs
{
    /// <summary>The Shoko file ID to scrobble.</summary>
    public int FileId { get; init; }

    /// <summary>The scrobble event type describing the playback event.</summary>
    public ScrobbleEventType EventType { get; init; }

    /// <summary>Current playback position in milliseconds.</summary>
    public double PositionMs { get; init; }

    /// <summary>Whether the item should be marked as watched, or null to let the server decide.</summary>
    public bool? Watched { get; init; }

    /// <summary>Whether this event should also persist user data (stream selections). Only true for "stop".</summary>
    public bool PersistUserData { get; init; }

    /// <summary>Selected video stream container ID, or null.</summary>
    public int? VideoStreamId { get; init; }

    /// <summary>Selected audio stream container ID, or null.</summary>
    public int? AudioStreamId { get; init; }

    /// <summary>Selected subtitle stream container ID, or null.</summary>
    public int? SubtitleStreamId { get; init; }
}

/// <summary>
/// Manages a single playback session: tracks position, pause state, metadata,
/// applies lazy sync gating (skip count, tick threshold, position delta),
/// and emits scrobble and Discord presence events for the coordinator to handle.
/// </summary>
public class PlaybackSessionManager
{
    /// <summary>Raised when a scrobble should be sent to the Shoko server.</summary>
    public event EventHandler<ScrobbleRequestEventArgs>? ScrobbleRequested;

    /// <summary>Raised when the Discord presence should be updated, or null to clear.</summary>
    public event EventHandler<DiscordPresenceData?>? DiscordPresenceChanged;

    private PlaybackSession? _session;

    private static readonly Regex EpisodeTitlePattern = new(@"^Episode\s+\d+$", RegexOptions.IgnoreCase);

    private const double AutoWatchRatio = 0.975;

    /// <summary>Whether a playback session is currently active.</summary>
    public bool HasActiveSession => _session is not null;

    /// <summary>The file ID of the current session, or null.</summary>
    public int? CurrentFileId => _session?.FileId;

    /// <summary>The last known playback position in milliseconds.</summary>
    public double CurrentPositionMs => _session?.PositionMs ?? 0;

    /// <summary>
    /// Start tracking a new playback session.
    /// </summary>
    public void StartSession(int fileId, double resumePositionMs, double durationMs, bool isRestricted,
        string? seriesTitle, string? episodeTitle, int epNumber, int epNumberRange, int epCount,
        string? posterUrl, int animeId)
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
            AnimeId = animeId,
            SkipEventCount = settings.SyncUserDataInitialSkipEventCount,
            TickThreshold = settings.SyncUserDataLiveScrobbleTickThreshold,
            PositionDeltaThresholdMs = settings.SyncUserDataLivePositionThresholdMs
        };

        EmitDiscordPresence();
    }

    /// <summary>
    /// Called on each time-pos change from mpv.
    /// </summary>
    public void OnPositionChanged(double positionMs)
    {
        if (_session is null) return;

        var delta = Math.Abs(positionMs - _session.LastScrobbledPositionMs);
        _session.PositionMs = positionMs;

        if (_session.IsPaused)
            return;

        // Skip tiny movements
        if (delta < _session.PositionDeltaThresholdMs && _session.ScrobbleTickCount > 0)
            return;

        // Throttle: only scrobble every N events
        if (++_session.ScrobbleTickCount < _session.TickThreshold)
            return;

        _session.ScrobbleTickCount = 0;
        _session.LastScrobbledPositionMs = positionMs;

        if (ShouldSendEvent())
            EmitScrobble(ScrobbleEventType.PlaybackProgress, positionMs, watched: null);
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
            // If we haven't sent a start event yet, send it now before pause
            if (!_session.SentStartEvent)
            {
                _session.SentStartEvent = true;
                EmitScrobble(ScrobbleEventType.PlaybackStart, _session.InitialPositionMs, watched: null);
            }

            if (ShouldSendEvent(isPauseOrResume: true))
                EmitScrobble(ScrobbleEventType.PlaybackPause, _session.PositionMs, watched: null);
        }
        else if (!isPaused && wasPaused)
        {
            if (!_session.SentStartEvent)
            {
                _session.SentStartEvent = true;
                EmitScrobble(ScrobbleEventType.PlaybackStart, _session.PositionMs, watched: null);
            }

            if (ShouldSendEvent(isPauseOrResume: true))
                EmitScrobble(ScrobbleEventType.PlaybackResume, _session.PositionMs, watched: null);

            _session.ScrobbleTickCount = 0;
        }

        EmitDiscordPresence();
    }

    /// <summary>
    /// Called when mpv hits end-of-file naturally.
    /// </summary>
    public void OnEofReached()
    {
        if (_session is null) return;

        _session.EofReached = true;
    }

    /// <summary>Set the selected video stream container ID (null = none).</summary>
    public void SetVideoStream(int? streamId)
    {
        if (_session is not null) _session.VideoStreamId = streamId;
    }

    /// <summary>Set the selected audio stream container ID (null = none).</summary>
    public void SetAudioStream(int? streamId)
    {
        if (_session is not null) _session.AudioStreamId = streamId;
    }

    /// <summary>Set the selected subtitle stream container ID (null = none/disabled).</summary>
    public void SetSubtitleStream(int? streamId)
    {
        if (_session is not null) _session.SubtitleStreamId = streamId;
    }

    /// <summary>
    /// End the current session, emitting a final stop scrobble.
    /// Returns the file ID that was being tracked, or null if no session.
    /// </summary>
    public int? EndSession(double finalPositionMs)
    {
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

        _session = null;

        if (SettingsProvider.Instance.Settings.PlaybackSyncingEnabled)
        {
            if (!isRestricted || !SettingsProvider.Instance.Settings.SkipRestrictedContent)
            {
                ScrobbleRequested?.Invoke(this, new ScrobbleRequestEventArgs
                {
                    FileId = fileId,
                    EventType = ScrobbleEventType.PlaybackEnd,
                    PositionMs = position,
                    Watched = watched,
                    PersistUserData = true,
                    VideoStreamId = videoStreamId,
                    AudioStreamId = audioStreamId,
                    SubtitleStreamId = subtitleStreamId
                });
            }
        }

        DiscordPresenceChanged?.Invoke(this, null);

        return fileId;
    }

    /// <summary>
    /// Called when the playlist advances to the next file (end-file with non-eof reason).
    /// Resets scrobble throttling state without ending the session.
    /// </summary>
    public void OnNextFile(int fileId, double resumePositionMs, double durationMs, bool isRestricted,
        string? seriesTitle, string? episodeTitle, int epNumber, int epNumberRange, int epCount,
        string? posterUrl, int animeId)
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
        _session.AnimeId = animeId;
        _session.ScrobbleTickCount = 0;
        _session.LastScrobbledPositionMs = resumePositionMs;
        _session.SentStartEvent = false;
        _session.SkipEventCount = SettingsProvider.Instance.Settings.SyncUserDataInitialSkipEventCount;
        _session.IsPaused = true;
        _session.EofReached = false;
        _session.VideoStreamId = null;
        _session.AudioStreamId = null;
        _session.SubtitleStreamId = null;

        EmitDiscordPresence();
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

    private void EmitScrobble(ScrobbleEventType eventType, double positionMs, bool? watched)
    {
        if (_session is null) return;

        if (!SettingsProvider.Instance.Settings.PlaybackSyncingEnabled)
            return;

        if (_session.IsRestricted && SettingsProvider.Instance.Settings.SkipRestrictedContent)
            return;

        ScrobbleRequested?.Invoke(this, new ScrobbleRequestEventArgs
        {
            FileId = _session.FileId,
            EventType = eventType,
            PositionMs = positionMs,
            Watched = watched
        });
    }

    private void EmitDiscordPresence()
    {
        if (_session is null)
        {
            DiscordPresenceChanged?.Invoke(this, null);
            return;
        }

        var settings = SettingsProvider.Instance.Settings;
        var privacy = settings.DiscordPrivacyMode;

        if (privacy)
        {
            DiscordPresenceChanged?.Invoke(this, new DiscordPresenceData(
                Details: "Watching Anime",
                State: null,
                LargeImageKey: null,
                LargeImageText: null,
                StartTimeStamp: _session.IsPaused ? null : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Buttons: _session.AnimeId > 0
                    ? new[] { new DiscordButtonData("View on AniDB", $"https://anidb.net/anime/{_session.AnimeId}") }
                    : null
            ));
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

        DiscordPresenceChanged?.Invoke(this, new DiscordPresenceData(
            Details: _session.SeriesTitle is not null ? $"Watching {_session.SeriesTitle}" : "Watching Anime",
            State: state,
            LargeImageKey: _session.PosterUrl ?? "shoko_default",
            LargeImageText: _session.SeriesTitle ?? "Shoko Server",
            StartTimeStamp: _session.IsPaused ? null : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Buttons: _session.AnimeId > 0
                ? new[] { new DiscordButtonData("View on AniDB", $"https://anidb.net/anime/{_session.AnimeId}") }
                : null
        ));
    }

    private class PlaybackSession
    {
        public int FileId;
        public double PositionMs;
        public double InitialPositionMs;
        public double LastScrobbledPositionMs;
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
        public int AnimeId;

        public bool SentStartEvent;
        public int ScrobbleTickCount;
        public int SkipEventCount;
        public int TickThreshold;
        public double PositionDeltaThresholdMs;

        public int? VideoStreamId;
        public int? AudioStreamId;
        public int? SubtitleStreamId;
    }
}