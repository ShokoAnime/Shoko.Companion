
namespace Shoko.Companion.Server.Models;

/// <summary>
/// A playlist episode entry without file data.
/// Use <see cref="PlaylistItemDto.Parts"/> for the associated video files.
/// </summary>
public class PlaylistEpisodeDto
{
    /// <summary>All IDs that may be useful for navigating.</summary>
    public PlaylistEpisodeIDsDto IDs { get; set; } = new();

    /// <summary>Episode title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Episode number.</summary>
    public int Number { get; set; }

    /// <summary>Episode type.</summary>
    public EpisodeType Type { get; set; }

    /// <summary>File size.</summary>
    public int Size { get; set; }

    /// <summary>Series title.</summary>
    public string SeriesTitle { get; set; } = string.Empty;

    /// <summary>Series poster image.</summary>
    public ImageDto? SeriesPoster { get; set; }

    /// <summary>Episode thumbnail.</summary>
    public ImageDto? Thumbnail { get; set; }
}
