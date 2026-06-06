using System.Collections.Generic;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO containing collections of image resources grouped by type (posters, backdrops, banners, logos, discs).
/// </summary>
public class ImagesDto
{
    /// <summary>
    /// The list of poster images associated with the entity.
    /// </summary>
    public List<object> Posters { get; set; } = [];

    /// <summary>
    /// The list of backdrop images associated with the entity.
    /// </summary>
    public List<object> Backdrops { get; set; } = [];

    /// <summary>
    /// The list of banner images associated with the entity.
    /// </summary>
    public List<object> Banners { get; set; } = [];

    /// <summary>
    /// The list of logo images associated with the entity.
    /// </summary>
    public List<object> Logos { get; set; } = [];

    /// <summary>
    /// The list of disc or media images associated with the entity.
    /// </summary>
    public List<object> Discs { get; set; } = [];
}
