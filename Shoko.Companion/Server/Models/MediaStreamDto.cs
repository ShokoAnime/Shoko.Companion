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
    /// MediaInfo's <c>StreamOrder</c>: the stream's position among <em>all</em>
    /// streams in the container, not within its own type — and reported as 0
    /// for every track of a kind in some files.
    ///
    /// It is therefore <em>not</em> the within-type ordinal Shoko persists in
    /// <see cref="VideoUserDataDto.LastAudioStreamIndex"/> and friends. That
    /// ordinal is the stream's position in its own kind's list
    /// (<see cref="MediaInfoDto.Audio"/>, <see cref="MediaInfoDto.Subtitles"/>),
    /// zero-based.
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
