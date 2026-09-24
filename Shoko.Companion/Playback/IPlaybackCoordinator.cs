using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Playback;

/// <summary>
/// Coordinates playback of Shoko media items, managing the full lifecycle
/// from URL resolution through mpv playback, scrobbling, and Discord presence.
/// </summary>
public interface IPlaybackCoordinator
{
    /// <summary>
    /// Gets the current playback state.
    /// </summary>
    PlaybackState CurrentState { get; }

    /// <summary>
    /// Gets the previous item in the play queue, or null when there is none.
    /// </summary>
    MediaItemInfoDto? PreviousItem { get; }

    /// <summary>
    /// Gets the currently playing queue item, or null when playback is
    /// stopped/idle.
    /// </summary>
    MediaItemInfoDto? CurrentItem { get; }

    /// <summary>
    /// Gets the next item in the play queue, or null when there is none
    /// (single-item queue, end of queue, or playback stopped/idle).
    /// </summary>
    MediaItemInfoDto? NextItem { get; }

    /// <summary>
    ///   Gets the full mpv playlist as observed from mpv itself — the
    ///   ground truth for the queue, including user-initiated navigation.
    ///   Each entry carries the same <see cref="MediaItemInfoDto.StreamUrl"/>
    ///   that is reported for that item in state updates, preserving exact
    ///   stream URL identity. Empty when there is no playlist.
    /// </summary>
    IReadOnlyList<MediaItemInfoDto> CurrentPlaylist { get; }

    /// <summary>
    ///   Gets or sets this companion's own media session id, pushed here by
    ///   <see cref="Server.MediaSessionClient"/> as it registers, reconnects
    ///   and disconnects. <c>null</c> whenever the companion holds none — the
    ///   plugin is absent, the hub is down, or registration has not returned
    ///   yet — which is what makes the APIv3 fallback in
    ///   <see cref="StreamUrls.ForPlayback"/> fire.
    /// </summary>
    Guid? MediaSessionId { get; set; }

    /// <summary>
    /// Gets the current playback position in seconds.
    /// </summary>
    double CurrentPositionSeconds { get; }

    /// <summary>
    /// Gets the duration of the currently playing media in seconds, if known.
    /// </summary>
    double? DurationSeconds { get; }

    /// <summary>
    /// Gets the effective volume level (0–<see cref="PlaybackCoordinator.MaxMpvVolume"/>).
    /// When mpv is connected this is the live mpv value; otherwise it falls
    /// back to the saved settings value, which defaults to 100 (matching
    /// mpv's own default).
    /// </summary>
    int CurrentVolume { get; }

    /// <summary>
    /// Gets the effective mute state. When mpv is connected this is the live
    /// mpv value; otherwise it falls back to the saved settings value.
    /// </summary>
    bool CurrentMuted { get; }

    /// <summary>
    ///   Gets the current playback speed multiplier from mpv (e.g. 1.0, 4.0).
    ///   Defaults to 1.0 when mpv is not connected or the value is unknown.
    /// </summary>
    double CurrentPlaybackSpeed { get; }

    /// <summary>
    /// Gets whether the mpv window is currently fullscreen. When mpv is
    /// connected this is the live mpv value; otherwise it falls back to the
    /// saved settings value, which defaults to true.
    /// </summary>
    bool? CurrentFullscreen { get; }

    /// <summary>
    ///   Gets the video, audio and subtitle ordinals currently playing, or
    ///   <c>null</c> when nothing is loaded.
    ///
    ///   Ordinals, not mpv's 1-based per-kind ids: the number Shoko stores
    ///   and the number another client can act on. A subtitle index of
    ///   <c>-1</c> means subtitles are off, which is a state a viewer chose
    ///   and not an absence of information.
    /// </summary>
    PlaybackTrackSelectionDto? CurrentTracks { get; }

    /// <summary>
    /// Raised when the playback state changes.
    /// </summary>
    event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    /// <summary>
    ///   Raised periodically (≈ every 10 s) during playback with the
    ///   current position so the media session hub stays in sync.
    /// </summary>
    event EventHandler<TimeSpan>? PositionTick;

    /// <summary>
    ///   Raised when the current volume or mute state changes.
    /// </summary>
    event EventHandler? VolumeStateChanged;

    /// <summary>
    ///   Raised when the current playback speed, fullscreen state or track
    ///   selection changes, so listeners can re-report state to the media
    ///   session hub.
    ///
    ///   Tracks ride here rather than on an event of their own because the
    ///   listener does the same thing for all three: send one full state
    ///   report. A fourth near-identical builder would be a fourth place
    ///   for a field to go missing.
    /// </summary>
    event EventHandler? ViewStateChanged;

    /// <summary>
    ///   Raised when the mpv playlist changes (add/remove/move/jump or
    ///   user-initiated navigation), so listeners can report the full
    ///   playlist to the media session hub via <c>UpdatePlaylist</c>.
    /// </summary>
    event EventHandler? PlaylistChanged;

    /// <summary>
    /// Play a shoko: URL (handles m3u8 ↔ JSON resolution, mpv launch, scrobble, etc).
    /// </summary>
    /// <param name="shokoUrl">
    ///   The shoko:// URL to play.
    /// </param>
    /// <param name="startPosition">
    ///   Optional. Start position of the first video stream from the url
    ///   playlist.
    /// </param>
    /// <param name="append">
    ///   Optional. Weather to append to the current playlist, or replace it.
    ///   Leave as <c>null</c> to leave it up to the coordinator. Set to
    ///   <c>true</c> to append, <c>false</c> to replace.
    /// </param>
    Task PlayAsync(string shokoUrl, TimeSpan? startPosition = null, bool? append = null);

    /// <summary>
    /// Stop playback and kill mpv if we launched it.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Pause mpv.
    /// </summary>
    Task PauseAsync();

    /// <summary>
    /// Resume mpv.
    /// </summary>
    Task ResumeAsync();

    /// <summary>
    /// Skip to the next item in the play queue, if any.
    /// </summary>
    Task SkipNextAsync();

    /// <summary>
    /// Skip to the previous item in the play queue, if any.
    /// </summary>
    Task SkipPreviousAsync();

    /// <summary>
    /// Seek to the specified position in seconds.
    /// </summary>
    Task SeekAsync(TimeSpan positionSeconds);

    /// <summary>
    /// Set the mpv volume and/or mute state. At least one of
    /// <paramref name="volume"/> or <paramref name="muted"/> must be non-null.
    /// </summary>
    /// <param name="volume">
    ///   Optional. Target volume (percent). Clamped to
    ///   0..<see cref="PlaybackCoordinator.MaxMpvVolume"/>.
    /// </param>
    /// <param name="muted">
    ///   Optional. Whether audio should be muted.
    /// </param>
    Task SetVolumeAsync(int? volume, bool? muted);

    /// <summary>
    ///   Set the mpv playback speed multiplier (e.g. 1.0 for normal speed,
    ///   4.0 for 4x). No-op when mpv is not connected.
    /// </summary>
    /// <param name="rate">The playback speed multiplier to apply.</param>
    Task SetPlaybackRateAsync(double rate);

    /// <summary>
    ///   Toggle the mpv window fullscreen state. No-op when mpv is not
    ///   connected.
    /// </summary>
    /// <param name="isFullscreen">
    ///   Whether the player window should be fullscreen.
    /// </param>
    Task SetFullscreenAsync(bool isFullscreen);

    /// <summary>
    ///   Switch the video, audio and/or subtitle track mpv is playing,
    ///   from ordinals. No-op when mpv is not connected or nothing is
    ///   loaded.
    ///
    ///   Per field: <c>null</c> says nothing and leaves that kind alone,
    ///   <c>-2</c> clears back to the file's own default, <c>-1</c> on the
    ///   subtitle index turns subtitles off, and <c>&gt;= 0</c> picks that
    ///   ordinal. An ordinal this file has no stream for is ignored rather
    ///   than refused - nobody asked for a failure, they asked for a track
    ///   the file turned out not to have.
    /// </summary>
    /// <param name="tracks">The requested selection.</param>
    Task SetTracksAsync(PlaybackTrackSelectionDto tracks);

    /// <summary>
    ///   Jump directly to the playlist item whose stream URL matches
    ///   <paramref name="streamUrl"/>, starting playback of that item.
    /// </summary>
    /// <param name="streamUrl">
    ///   The exact stream URL reported for the item in state updates.
    /// </param>
    Task JumpToPlaylistItemAsync(string streamUrl);

    /// <summary>
    ///   Add media to the mpv playlist, inserting at
    ///   <paramref name="atIndex"/> when given, appending otherwise. The new
    ///   full playlist is reported afterwards.
    /// </summary>
    /// <param name="items">
    ///   The items to resolve and add. Each carries its own start position,
    ///   applied when the queue reaches it.
    /// </param>
    /// <param name="atIndex">
    ///   Optional zero-based insertion index; <c>null</c> appends.
    /// </param>
    Task AddToPlaylistAsync(IReadOnlyList<PlaylistAddition> items, int? atIndex);

    /// <summary>
    ///   Remove items whose stream URL matches an entry in
    ///   <paramref name="streamUrls"/> from the mpv playlist.
    ///   The new full playlist is reported afterwards.
    /// </summary>
    /// <param name="streamUrls">
    ///   The exact stream URLs of the items to remove.
    /// </param>
    Task RemoveFromPlaylistAsync(IReadOnlyList<string> streamUrls);

    /// <summary>
    ///   Move a playlist item from one index to another in mpv.
    ///   The new full playlist is reported afterwards.
    /// </summary>
    /// <param name="fromIndex">
    ///   Zero-based source index.
    /// </param>
    /// <param name="toIndex">
    ///   Zero-based destination index (final position).
    /// </param>
    Task MovePlaylistItemAsync(int fromIndex, int toIndex);

    /// <summary>
    ///   Capture a video frame. When <paramref name="position"/> is null,
    ///   captures the current frame from the active playback instance.
    ///   When set, seeks to that position on a headless mpv slave and
    ///   captures there.
    /// </summary>
    /// <param name="position">
    ///   Optional seek position. Null for current frame, or a specific
    ///   position within the currently playing file.
    /// </param>
    Task<byte[]?> CaptureScreenshotAsync(TimeSpan? position = null);

    /// <summary>
    ///   Show a text message on the mpv OSD, if mpv is connected.
    ///   No-op when mpv is not running or disconnected.
    /// </summary>
    /// <param name="text">The message to display.</param>
    /// <param name="durationMs">Display duration in milliseconds (default 3000).</param>
    Task ShowOsdTextAsync(string text, int durationMs = 3000);
}
