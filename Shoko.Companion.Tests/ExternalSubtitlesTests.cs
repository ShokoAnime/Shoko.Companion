using Shoko.Companion.Playback;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   Which subtitle files beside a video are handed to mpv. The order is
///   the point: a subtitle's position in Shoko's list is its mpv sid.
/// </summary>
public class ExternalSubtitlesTests
{
    private static MediaStreamDto Embedded(string lang) => new() { LanguageCode = lang };

    private static MediaStreamDto External(string file, string lang = "eng")
        => new() { IsExternal = true, ExternalFilename = file, LanguageCode = lang };

    private static string?[] Files(MediaInfoDto mi)
        => ExternalSubtitles.ToAdd(mi).Select(s => s.ExternalFilename).ToArray();

    [Fact]
    public void AddsTheTrailingExternalsInOrder()
    {
        var mi = new MediaInfoDto
        {
            Subtitles = [Embedded("jpn"), External("a.en.ass"), External("a.de.srt", "ger")],
        };

        Assert.Equal(new string?[] { "a.en.ass", "a.de.srt" }, Files(mi));
    }

    [Fact]
    public void NoExternalsAddsNothing()
    {
        Assert.Empty(ExternalSubtitles.ToAdd(new MediaInfoDto { Subtitles = [Embedded("jpn")] }));
    }

    [Fact]
    public void AnEmbeddedStreamAfterAnExternalOneAddsNothing()
    {
        // The position mapping cannot hold, so nothing is added rather than
        // something a restore would select wrongly.
        var mi = new MediaInfoDto { Subtitles = [External("a.ass"), Embedded("jpn")] };

        Assert.Empty(ExternalSubtitles.ToAdd(mi));
    }

    [Fact]
    public void StopsAtTheFirstFileMpvCannotLoad()
    {
        // VobSub cannot be loaded over HTTP; skipping it and adding the next
        // would give that file VobSub's position.
        var mi = new MediaInfoDto
        {
            Subtitles = [External("a.ass"), External("a.idx"), External("a.srt")],
        };

        Assert.Equal(new string?[] { "a.ass" }, Files(mi));
    }

    [Fact]
    public void StopsAtAnExternalWithoutAFilename()
    {
        var mi = new MediaInfoDto
        {
            Subtitles = [External("a.ass"), new MediaStreamDto { IsExternal = true }, External("a.srt")],
        };

        Assert.Equal(new string?[] { "a.ass" }, Files(mi));
    }
}
