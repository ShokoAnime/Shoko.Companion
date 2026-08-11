using Newtonsoft.Json;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// Which video, audio and subtitle track a session is playing with,
/// mirroring the Media Session plugin's <c>PlaybackTrackSelection</c>.
///
/// Ordinals, not container stream IDs: the position among streams of the
/// same kind, counting from zero, which is what Shoko persists and the
/// only number that means the same thing to two clients holding two
/// different files for one episode.
///
/// Each field is three-valued on the wire, because JSON cannot tell an
/// absent field from an explicit null:
///
/// <list type="bullet">
///   <item><c>null</c> says nothing, and leaves that kind alone.</item>
///   <item><c>-2</c> is the verb that clears a choice back to the file's
///     own default.</item>
///   <item><c>-1</c> only on <see cref="SubtitleIndex"/>, and a state
///     rather than a verb: subtitles deliberately off.</item>
///   <item><c>&gt;= 0</c> picks that ordinal.</item>
/// </list>
///
/// It travels in both directions and means different things each way. On
/// a <c>SetTracks</c> command it is a request, and the plugin records
/// nothing from it. On <c>UpdateState</c> it is this session's report of
/// what it is actually playing, and that is the only thing the plugin
/// stores and the only thing a handoff carries onwards.
/// </summary>
public sealed class PlaybackTrackSelectionDto
{
    /// <summary>
    /// Ordinal among the video streams.
    /// </summary>
    [JsonProperty("VideoOrdinal")]
    public int? VideoOrdinal { get; init; }

    /// <summary>
    /// Ordinal among the audio streams.
    /// </summary>
    [JsonProperty("AudioOrdinal")]
    public int? AudioOrdinal { get; init; }

    /// <summary>
    /// Ordinal among the subtitle streams, or <c>-1</c> for off.
    /// </summary>
    [JsonProperty("SubtitleIndex")]
    public int? SubtitleIndex { get; init; }
}
