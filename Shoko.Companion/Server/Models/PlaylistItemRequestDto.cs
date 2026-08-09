using System;
using Newtonsoft.Json;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO for one item the hub asks us to queue, mirroring the plugin's
/// <c>PlaylistItemRequest</c>.
///
/// A queued item is a play request in its own right — the same thing
/// <c>PlaybackRequest</c> says, minus <c>Append</c>, since replace-or-append
/// is a statement about the playlist as a whole and the insert index already
/// places the item. That is why this is a video ID plus a start position and
/// not a bare video ID: an episode queued half-watched resumes where one
/// played directly does.
///
/// The plugin also sends track selection fields. They are omitted here for
/// the same reason <see cref="Shoko.Companion.Server.PlaybackRequestDto"/>
/// omits them: mpv picks its own tracks on the Play path too, and a field we ignore
/// is one somebody eventually expects us to honour. Newtonsoft drops the
/// extra JSON.
/// </summary>
public sealed class PlaylistItemRequestDto
{
    /// <summary>
    /// Shoko video ID to queue. The companion resolves this to a stream URL
    /// through the playlist/stream pipeline.
    /// </summary>
    [JsonProperty("VideoId")]
    public int VideoId { get; init; }

    /// <summary>
    ///   Optional. Where this item starts when the queue reaches it. The
    ///   server resolves the viewer's stored progress before sending, so
    ///   what arrives is a position and not a question — including
    ///   <see cref="TimeSpan.Zero"/>, which means the beginning and not
    ///   "unset".
    /// </summary>
    [JsonProperty("StartPosition")]
    public TimeSpan? StartPosition { get; init; }
}
