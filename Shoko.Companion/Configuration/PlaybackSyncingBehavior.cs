namespace Shoko.Companion.Configuration;

/// <summary>
/// Controls what playback events are synced to the Shoko server.
/// Maps to the Shokofin scrobble tiers.
/// </summary>
public enum PlaybackSyncingBehavior
{
    /// <summary>
    /// Only send a stop event when playback finishes.
    /// No events during playback (no play/pause/resume/progress).
    /// </summary>
    AfterPlayback,

    /// <summary>
    /// Send events on every playback state change (play, pause, resume, stop).
    /// No periodic live progress updates.
    /// </summary>
    OnEveryEvent,

    /// <summary>
    /// Send events on every playback state change AND send periodic
    /// live progress updates during playback (scrobbles).
    /// </summary>
    LiveSync,
}
