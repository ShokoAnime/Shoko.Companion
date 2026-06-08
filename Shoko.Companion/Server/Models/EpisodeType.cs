using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// The type of an episode as defined by AniDB.
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum EpisodeType
{
    /// <summary>The episode type is unknown.</summary>
    Unknown = 0,

    /// <summary>A catch-all type for future extensions.</summary>
    Other = 1,

    /// <summary>A normal episode.</summary>
    Episode = 2,

    /// <summary>A special episode.</summary>
    Special = 3,

    /// <summary>A trailer.</summary>
    Trailer = 4,

    /// <summary>An opening song, ending song, or other type of credits.</summary>
    Credits = 5,

    /// <summary>AniDB parody type.</summary>
    Parody = 6,
}
