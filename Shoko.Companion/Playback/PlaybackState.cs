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
    Error
}
