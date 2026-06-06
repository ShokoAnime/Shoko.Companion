using System.Collections.Generic;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing the media info (stream layout) of a file, returned from the
/// playlist endpoint when <c>include=MediaInfo</c> is requested.
/// </summary>
public class MediaInfoDto
{
    /// <summary>
    /// Video streams in container order.
    /// </summary>
    public List<MediaStreamDto> Video { get; set; } = [];

    /// <summary>
    /// Audio streams in container order. The 1-based position maps to mpv's <c>aid</c>.
    /// </summary>
    public List<MediaStreamDto> Audio { get; set; } = [];

    /// <summary>
    /// Subtitle streams in container order. The 1-based position maps to mpv's <c>sid</c>.
    /// </summary>
    public List<MediaStreamDto> Subtitles { get; set; } = [];
}
