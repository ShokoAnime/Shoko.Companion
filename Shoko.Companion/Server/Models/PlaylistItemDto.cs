using System.Collections.Generic;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing a single item in a playlist, containing the primary episode,
/// any additional episodes, and the media file parts that compose it.
/// </summary>
public class PlaylistItemDto
{
    /// <summary>The primary episode for this playlist item.</summary>
    public PlaylistEpisodeDto Episode { get; set; } = new();

    /// <summary>Additional episodes bundled with this item.</summary>
    public List<PlaylistEpisodeDto> AdditionalEpisodes { get; set; } = [];

    /// <summary>The media file parts that make up this item.</summary>
    public List<FileDto> Parts { get; set; } = [];
}
