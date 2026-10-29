using System;
using Newtonsoft.Json;

namespace Shoko.Companion.Server.Models;

/// <summary>
///   One item of this companion's own session as the media session plugin
///   describes it: the plugin's <c>MediaItemInfo</c>, read in the direction
///   the server sends it.
///
///   <para>
///     <b>Not <see cref="Shoko.Companion.Server.MediaItemInfoDto"/>.</b>
///     That one is what this client <em>reports</em>, and its title is ours;
///     this is what the server answers, and its titles are the server's.
///     <c>Title</c> is left out on purpose: the plugin fills it from the
///     report, so it is our own title handed back, and reading it as the
///     server's answer would be reading ourselves.
///   </para>
///   <para>
///     <b>What the server says an item is, this client shows.</b> The plugin
///     resolves an item's series and episode once, and every other client
///     shows that answer; a companion that kept its own from Shoko's playlist
///     API would show a different name for the same thing whenever the two
///     disagree. They will: the plugin is moving that answer behind a
///     provider another plugin can supply (Kaname #309), whose titles are
///     not derivable from Shoko's.
///   </para>
///   <para>
///     Only the fields this client reads. Newtonsoft drops the rest, and an
///     enum the plugin may grow is read as a string so a value this build has
///     never seen cannot fail the whole message's binding. The opaque
///     <c>SeriesId</c> / <c>EpisodeId</c> and the TMDB ids #309 adds belong
///     here once they ship, for correlating an item rather than naming it.
///   </para>
/// </summary>
public sealed record SessionItemInfoDto
{
    /// <summary>
    ///   The server's opaque handle for this item within the session.
    /// </summary>
    [JsonProperty("Id")]
    public Guid Id { get; init; }

    /// <summary>
    ///   Whether the server ruled this item private. The session's own copy
    ///   is never masked, so the titles are present either way.
    /// </summary>
    [JsonProperty("IsPrivate")]
    public bool IsPrivate { get; init; }

    /// <summary>
    ///   The Shoko video the item plays, which is what ties it to a file
    ///   this client has loaded.
    /// </summary>
    [JsonProperty("VideoId")]
    public int? VideoId { get; init; }

    /// <summary>
    ///   The stream URL the item was reported with.
    /// </summary>
    [JsonProperty("StreamUrl")]
    public string? StreamUrl { get; init; }

    /// <summary>
    ///   The episode's title, as the server resolved it.
    /// </summary>
    [JsonProperty("EpisodeTitle")]
    public string? EpisodeTitle { get; init; }

    /// <summary>
    ///   The episode's number within its series.
    /// </summary>
    [JsonProperty("EpisodeNumber")]
    public int? EpisodeNumber { get; init; }

    /// <summary>
    ///   The episode's type, lowercase on the wire (<c>episode</c>,
    ///   <c>special</c>, <c>credits</c>, <c>trailer</c>, <c>parody</c>,
    ///   <c>other</c>).
    /// </summary>
    [JsonProperty("EpisodeType")]
    public string? EpisodeType { get; init; }

    /// <summary>
    ///   How many episodes the series has, if known.
    /// </summary>
    [JsonProperty("EpisodeCount")]
    public int? EpisodeCount { get; init; }

    /// <summary>
    ///   The series' title, as the server resolved it.
    /// </summary>
    [JsonProperty("SeriesTitle")]
    public string? SeriesTitle { get; init; }
}
