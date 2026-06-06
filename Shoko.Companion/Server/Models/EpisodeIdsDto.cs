using System.Collections.Generic;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO containing identifiers for an episode across multiple data sources (AniDB, TvDB, IMDb, TMDB).
/// </summary>
public class EpisodeIdsDto
{
    /// <summary>
    /// The internal Shoko server ID for the episode.
    /// </summary>
    public int ID { get; set; }

    /// <summary>
    /// The ID of the parent series that this episode belongs to.
    /// </summary>
    public int ParentSeries { get; set; }

    /// <summary>
    /// The AniDB ID for the episode.
    /// </summary>
    public int AniDB { get; set; }

    /// <summary>
    /// The list of TvDB IDs associated with the episode.
    /// </summary>
    public List<int> TvDB { get; set; } = [];

    /// <summary>
    /// The list of IMDb IDs associated with the episode.
    /// </summary>
    public List<string> IMDB { get; set; } = [];

    /// <summary>
    /// The TMDB identifiers for the episode, if available.
    /// </summary>
    public TMDBIdsDto? TMDB { get; set; }
}
