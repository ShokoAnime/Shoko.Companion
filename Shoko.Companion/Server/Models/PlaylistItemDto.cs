using System.Collections.Generic;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing a single item in a playlist, containing the primary episode, any additional episodes, and the media file parts that compose it.
/// </summary>
public class PlaylistItemDto
{
    /// <summary>
    /// The primary episode for this playlist item, if available.
    /// </summary>
    public EpisodeDto? Episode { get; set; }

    /// <summary>
    /// Additional episodes bundled with this playlist item (e.g., for multi-episode files).
    /// </summary>
    public List<EpisodeDto> AdditionalEpisodes { get; set; } = [];

    /// <summary>
    /// The media file parts that make up this playlist item.
    /// </summary>
    public List<FileDto> Parts { get; set; } = [];
}
