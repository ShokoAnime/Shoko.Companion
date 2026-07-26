using System;
using Newtonsoft.Json;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing user-specific video playback data such as progress position,
/// watch count, and last-used stream selections.
/// </summary>
public class VideoUserDataDto
{
    /// <summary>
    /// The playback progress position within the video.
    /// </summary>
    [JsonProperty("ProgressPosition")]
    public TimeSpan? ProgressPosition { get; set; }

    /// <summary>
    /// The number of times the video has been watched.
    /// </summary>
    [JsonProperty("WatchedCount")]
    public int WatchedCount { get; set; }

    /// <summary>
    /// The timestamp when the video was last watched.
    /// </summary>
    [JsonProperty("LastWatchedAt")]
    public DateTime? LastWatchedAt { get; set; }

    /// <summary>
    /// The timestamp when the user data was last updated.
    /// </summary>
    [JsonProperty("LastUpdatedAt")]
    public DateTime LastUpdatedAt { get; set; }

    /// <summary>
    /// The container stream ID of the last-used video stream.
    /// </summary>
    [JsonProperty("LastVideoStreamIndex")]
    public int? LastVideoStreamIndex { get; set; }

    /// <summary>
    /// The container stream ID of the last-used audio stream.
    /// </summary>
    [JsonProperty("LastAudioStreamIndex")]
    public int? LastAudioStreamIndex { get; set; }

    /// <summary>
    /// The container stream ID of the last-used subtitle stream.
    /// </summary>
    [JsonProperty("LastSubtitleStreamIndex")]
    public int? LastSubtitleStreamIndex { get; set; }
}
