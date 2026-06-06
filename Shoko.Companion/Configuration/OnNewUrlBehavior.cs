namespace Shoko.Companion.Configuration;

/// <summary>
/// Controls what happens when a new shoko: URL arrives while something is playing.
/// </summary>
public enum OnNewUrlBehavior
{
    /// <summary>
    /// Stop current playback and start the new URL.
    /// </summary>
    Replace,

    /// <summary>
    /// Silently ignore the new URL while something is playing.
    /// </summary>
    Ignore,

    /// <summary>
    /// Append the new URL to the mpv playlist without interrupting playback.
    /// </summary>
    Append,
}
