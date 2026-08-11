namespace Shoko.Companion.Playback;

/// <summary>
/// The family a recognised stream URL belongs to.
/// </summary>
public enum StreamUrlKind
{
    /// <summary>
    /// Shoko's own APIv3 stream endpoint, <c>/api/v3/File/{id}/Stream</c>.
    /// Authenticated by API key, with no session concept.
    /// </summary>
    ApiV3,

    /// <summary>
    /// A Media Session plugin stream endpoint,
    /// <c>/api/plugin/MediaSession/v1/Stream/{videoId}[/...]</c>. Anonymous, and
    /// guarded on a <c>sessionId</c> query parameter naming a session whose
    /// current, next or previous item is the requested video.
    /// </summary>
    MediaSession,
}
