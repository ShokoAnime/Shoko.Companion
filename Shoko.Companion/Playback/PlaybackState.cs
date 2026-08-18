namespace Shoko.Companion.Playback;

/// <summary>
/// Represents the current state of playback.
/// </summary>
public enum PlaybackState
{
    /// <summary>
    /// No active playback.
    /// </summary>
    Idle,

    /// <summary>
    /// Loading media and preparing playback.
    /// </summary>
    Loading,

    /// <summary>
    /// Media is currently playing.
    /// </summary>
    Playing,

    /// <summary>
    /// Playback is paused.
    /// </summary>
    Paused,

    /// <summary>
    /// Playback has been stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// An error occurred during playback.
    /// </summary>
    Error,

    /// <summary>
    /// Playback has started and stalled waiting for data. The file is
    /// loaded, the position is real and a frame is on screen — only the
    /// cache ran dry, which is what separates this from
    /// <see cref="Loading"/>, where nothing exists to show yet.
    ///
    /// <para>
    ///   Read from mpv's <c>paused-for-cache</c>, which is a stall the
    ///   viewer did not ask for and is reported independently of
    ///   <c>pause</c>. Appended rather than slotted next to
    ///   <see cref="Playing"/> so the existing ordinals do not move.
    /// </para>
    /// </summary>
    Buffering
}
