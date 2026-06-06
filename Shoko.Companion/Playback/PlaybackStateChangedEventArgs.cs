using System;

namespace Shoko.Companion.Playback;

/// <summary>
/// Provides data for the <see cref="IPlaybackCoordinator.StateChanged"/> event.
/// </summary>
public class PlaybackStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the previous playback state.
    /// </summary>
    public PlaybackState OldState { get; }

    /// <summary>
    /// Gets the new playback state.
    /// </summary>
    public PlaybackState NewState { get; }

    /// <summary>
    /// Gets an optional error message when transitioning to the <see cref="PlaybackState.Error"/> state.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Gets the title of the currently playing item, if applicable.
    /// </summary>
    public string? NowPlaying { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="oldState">The previous playback state.</param>
    /// <param name="newState">The new playback state.</param>
    /// <param name="errorMessage">Optional error message for error state transitions.</param>
    /// <param name="nowPlaying">Optional title of the currently playing item.</param>
    public PlaybackStateChangedEventArgs(PlaybackState oldState, PlaybackState newState, string? errorMessage = null, string? nowPlaying = null)
    {
        OldState = oldState;
        NewState = newState;
        ErrorMessage = errorMessage;
        NowPlaying = nowPlaying;
    }
}
