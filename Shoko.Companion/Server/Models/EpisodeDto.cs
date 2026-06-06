using System;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing an episode from the Shoko server, including metadata such as identifiers, images, duration, and user data.
/// </summary>
public class EpisodeDto
{
    /// <summary>
    /// The collection of identifiers for this episode across various data sources.
    /// </summary>
    public EpisodeIdsDto IDs { get; set; } = new();

    /// <summary>
    /// Indicates whether the episode has a custom name override.
    /// </summary>
    public bool HasCustomName { get; set; }

    /// <summary>
    /// The description or synopsis of the episode.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the episode is marked as a favorite.
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Image resources associated with the episode (posters, backdrops, etc.).
    /// </summary>
    public ImagesDto? Images { get; set; }

    /// <summary>
    /// The total duration of the episode.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// The position at which playback was last resumed, if available.
    /// </summary>
    public TimeSpan? ResumePosition { get; set; }

    /// <summary>
    /// The number of times the episode has been watched.
    /// </summary>
    public int WatchCount { get; set; }

    /// <summary>
    /// Indicates whether the episode is hidden from normal listing.
    /// </summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// The user's rating for the episode, if any.
    /// </summary>
    public object? UserRating { get; set; }

    /// <summary>
    /// The timestamp when the episode was last watched.
    /// </summary>
    public DateTime? Watched { get; set; }

    /// <summary>
    /// The timestamp when the episode record was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The timestamp when the episode record was last updated.
    /// </summary>
    public DateTime Updated { get; set; }

    /// <summary>
    /// The display name or title of the episode.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The file size of the episode in bytes.
    /// </summary>
    public int Size { get; set; }
}
