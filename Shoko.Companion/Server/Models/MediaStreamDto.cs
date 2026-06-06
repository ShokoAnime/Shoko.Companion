namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing a single media stream (video, audio, or subtitle) within a file's media info.
/// </summary>
public class MediaStreamDto
{
    /// <summary>
    /// The absolute container stream ID (unique across all stream types in the file).
    /// </summary>
    public int ID { get; set; }

    /// <summary>
    /// The relative order of this stream within its own stream type.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// The full language name (e.g. "Japanese").
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// The ISO language code (e.g. "jpn").
    /// </summary>
    public string? LanguageCode { get; set; }

    /// <summary>
    /// The stream title, if any.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Whether this stream is marked as default in the container.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Whether this stream is marked as forced.
    /// </summary>
    public bool IsForced { get; set; }
}
