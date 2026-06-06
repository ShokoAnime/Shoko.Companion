using System;
using System.Collections.Generic;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing a media file known to the Shoko server, including hashes, locations, and playback metadata.
/// </summary>
public class FileDto
{
    /// <summary>
    /// The internal Shoko server ID for the file.
    /// </summary>
    public int ID { get; set; }

    /// <summary>
    /// The size of the file in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Indicates whether this file is a variation (alternate quality, format, etc.) of another file.
    /// </summary>
    public bool IsVariation { get; set; }

    /// <summary>
    /// Indicates whether this file is ignored by the server's import process.
    /// </summary>
    public bool IsIgnored { get; set; }

    /// <summary>
    /// The collection of cryptographic hashes computed for this file.
    /// </summary>
    public List<HashDto> Hashes { get; set; } = [];

    /// <summary>
    /// The physical locations where this file resides on disk.
    /// </summary>
    public List<LocationDto> Locations { get; set; } = [];

    /// <summary>
    /// AVDump status and progress information for this file, if available.
    /// </summary>
    public AVDumpDto? AVDump { get; set; }

    /// <summary>
    /// The resolution of the file (e.g., "1920x1080").
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// The total duration of the file.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// The position at which playback was last resumed, if available.
    /// </summary>
    public TimeSpan? ResumePosition { get; set; }

    /// <summary>
    /// The timestamp when the file was last viewed.
    /// </summary>
    public DateTime? Viewed { get; set; }

    /// <summary>
    /// The timestamp when the file was last fully watched.
    /// </summary>
    public DateTime? Watched { get; set; }

    /// <summary>
    /// The timestamp when the file was imported into the server.
    /// </summary>
    public DateTime? Imported { get; set; }

    /// <summary>
    /// The timestamp when the file record was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The timestamp when the file record was last updated.
    /// </summary>
    public DateTime Updated { get; set; }

    /// <summary>
    /// Media stream info (video/audio/subtitle layout). Only populated when the
    /// playlist request includes <c>include=MediaInfo</c>.
    /// </summary>
    public MediaInfoDto? MediaInfo { get; set; }
}
