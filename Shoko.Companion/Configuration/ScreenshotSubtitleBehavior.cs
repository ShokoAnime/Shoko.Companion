namespace Shoko.Companion.Configuration;

/// <summary>
/// Controls when mpv subtitles are hidden before capturing a screenshot.
/// </summary>
public enum ScreenshotSubtitleBehavior
{
    /// <summary>Never hide subtitles; they will appear in screenshots.</summary>
    Disabled,

    /// <summary>Hide subtitles only when playback is paused (no visual flicker).</summary>
    OnlyWhenPaused,

    /// <summary>Always hide subtitles before capture, even during active playback.</summary>
    Always,
}
