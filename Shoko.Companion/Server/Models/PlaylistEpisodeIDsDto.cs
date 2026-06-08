namespace Shoko.Companion.Server.Models;

/// <summary>
/// IDs for a <see cref="PlaylistEpisodeDto"/>.
/// </summary>
public class PlaylistEpisodeIDsDto
{
    /// <summary>The related AniDB episode id.</summary>
    public int AnidbEpisode { get; set; }

    /// <summary>The related AniDB anime id.</summary>
    public int AnidbAnime { get; set; }

    /// <summary>The related Shoko episode id, if available locally.</summary>
    public int ShokoEpisode { get; set; }

    /// <summary>The related Shoko series id, if available locally.</summary>
    public int ShokoSeries { get; set; }

    /// <summary>The first TMDB show id linked to the episode's series.</summary>
    public int? TmdbShow { get; set; }

    /// <summary>The first TMDB movie id linked to the episode.</summary>
    public int? TmdbMovie { get; set; }

    /// <summary>The TVDB show id linked to the first TMDB show.</summary>
    public int? TvdbShow { get; set; }

    /// <summary>The IMDB movie id linked to the first TMDB movie.</summary>
    public string? ImdbMovie { get; set; }
}
