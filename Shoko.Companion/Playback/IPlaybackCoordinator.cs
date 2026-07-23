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
    /// Raised when the playback state changes.
    /// </summary>
    event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

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
}
