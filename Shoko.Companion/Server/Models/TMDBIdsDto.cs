using System.Collections.Generic;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO containing TMDB (The Movie Database) identifiers for episodes, movies, and shows.
/// </summary>
public class TMDBIdsDto
{
    /// <summary>
    /// The list of TMDB episode IDs associated with the entity.
    /// </summary>
    public List<int> Episode { get; set; } = [];

    /// <summary>
    /// The list of TMDB movie IDs associated with the entity.
    /// </summary>
    public List<int> Movie { get; set; } = [];

    /// <summary>
    /// The list of TMDB show (series) IDs associated with the entity.
    /// </summary>
    public List<int> Show { get; set; } = [];
}
