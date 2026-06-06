namespace Shoko.Companion.Server.Models;

/// <summary>
/// Mirror of the server's VideoUserDataSaveReason enum values.
/// </summary>
public enum ScrobbleEventType
{
    /// <summary>
    /// No scrobble event; used as a default or uninitialized value.
    /// </summary>
    None = 0,

    /// <summary>
    /// A scrobble event triggered by direct user interaction.
    /// </summary>
    UserInteraction = 1,

    /// <summary>
    /// A scrobble event marking the start of playback.
    /// </summary>
    PlaybackStart = 2,

    /// <summary>
    /// A scrobble event marking a pause in playback.
    /// </summary>
    PlaybackPause = 3,

    /// <summary>
    /// A scrobble event marking the resumption of playback after a pause.
    /// </summary>
    PlaybackResume = 4,

    /// <summary>
    /// A scrobble event reporting periodic playback progress.
    /// </summary>
    PlaybackProgress = 5,

    /// <summary>
    /// A scrobble event marking the end of playback.
    /// </summary>
    PlaybackEnd = 6,

    /// <summary>
    /// A scrobble event triggered by a file import.
    /// </summary>
    Import = 7
}

/// <summary>
/// Extension methods for the <see cref="ScrobbleEventType"/> enum.
/// </summary>
public static class ScrobbleEventTypeExtensions
{
    /// <summary>
    /// Converts the scrobble event type to its query-string value for the Shoko server API.
    /// </summary>
    /// <param name="type">The scrobble event type to convert.</param>
    /// <returns>A string representing the query value (e.g., "play", "pause", "stop").</returns>
    public static string ToQueryValue(this ScrobbleEventType type) => type switch
    {
        ScrobbleEventType.PlaybackStart => "play",
        ScrobbleEventType.PlaybackPause => "pause",
        ScrobbleEventType.PlaybackResume => "resume",
        ScrobbleEventType.PlaybackProgress => "scrobble",
        ScrobbleEventType.PlaybackEnd => "stop",
        _ => "user-interaction"
    };
}
