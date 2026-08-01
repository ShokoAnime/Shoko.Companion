using System;
using System.Threading.Tasks;

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
    /// Gets the file ID of the currently playing media, if any.
    /// </summary>
    int? CurrentFileId { get; }

    /// <summary>
    /// Gets the current playback position in seconds.
    /// </summary>
    double CurrentPositionSeconds { get; }

    /// <summary>
    /// Gets the duration of the currently playing media in seconds, if known.
    /// </summary>
    double? DurationSeconds { get; }

    /// <summary>
    /// Gets the title of the currently playing media, if any.
    /// </summary>
    string? CurrentTitle { get; }

    /// <summary>
    /// Gets the stream URL of the currently playing media, if any.
    /// </summary>
    string? CurrentStreamUrl { get; }

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
