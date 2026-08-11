using Shoko.Companion.Playback;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   Covers the two stream URL shapes the companion can meet and the
///   session-id policy applied to the media session one. The policy is the half that
///   is not a regex: every media session URL the companion plays carries the
///   companion's own session id, and when it holds none the URL is swapped
///   back for APIv3 rather than played under a stranger's session.
/// </summary>
public class StreamUrlsTests
{
    private const string Base = "http://localhost:8111";

    private static readonly Guid Ours = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Theirs = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── Recognising the two shapes ──────────────────────────────────────

    [Theory]
    [InlineData($"{Base}/api/v3/File/42/Stream", 42)]
    [InlineData($"{Base}/api/v3/File/42/Stream?apikey=abc&epNo=3", 42)]
    [InlineData($"{Base}/shoko/api/v3/File/7/Stream", 7)]
    public void Parse_ApiV3_ReadsVideoId(string url, int expected)
    {
        var info = StreamUrls.Parse(url);

        Assert.NotNull(info);
        Assert.Equal(StreamUrlKind.ApiV3, info.Kind);
        Assert.Equal(expected, info.VideoId);
        Assert.Null(info.SessionId);
    }

    [Theory]
    // The bare full-file stream, and every shape the plugin's Stream routes
    // serve under it — masters, media playlists, transcode and remux init
    // segments and media segments, Info, subtitles and attachments.
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42", 42, null)]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42?mode=auto", 42, null)]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8", 42, "master.m3u8")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?videoOrdinal=1", 42, "master.m3u8")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/media.m3u8?video=true", 42, "media.m3u8")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Transcode/Video/init.mp4", 42, "Transcode/Video/init.mp4")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Transcode/Audio/12.m4s", 42, "Transcode/Audio/12.m4s")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Remux/Video/3.m4s", 42, "Remux/Video/3.m4s")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Remux/Audio/init.mp4", 42, "Remux/Audio/init.mp4")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Info", 42, "Info")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Subtitle/2", 42, "Subtitle/2")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Attachment/0", 42, "Attachment/0")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Preview", 42, "Preview")]
    // A Shoko behind a path base serves the plugin under it too.
    [InlineData($"{Base}/shoko/api/plugin/MediaSession/v1/Stream/9/master.m3u8", 9, "master.m3u8")]
    // #178: a file with several video tracks gets one master per track. The
    // plugin distinguishes them by videoOrdinal, the CLI's static output by
    // filename; the video id sits in the same path segment either way.
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/28204/master-video2.m3u8", 28204, "master-video2.m3u8")]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/28204/video3-audio5.m3u8", 28204, "video3-audio5.m3u8")]
    public void Parse_MediaSession_ReadsVideoIdAndResource(string url, int expectedId, string? expectedResource)
    {
        var info = StreamUrls.Parse(url);

        Assert.NotNull(info);
        Assert.Equal(StreamUrlKind.MediaSession, info.Kind);
        Assert.Equal(expectedId, info.VideoId);
        Assert.Equal(expectedResource, info.Resource);
    }

    [Fact]
    public void Parse_MediaSession_ReadsSessionId()
    {
        var info = StreamUrls.Parse($"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?sessionId={Theirs}");

        Assert.NotNull(info);
        Assert.Equal(Theirs, info.SessionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#EXTINF:-1,Some Show - 1 - Title")]
    [InlineData("#EXTM3U")]
    [InlineData("/home/user/Videos/episode.mkv")]
    [InlineData($"{Base}/api/v3/Image/TMDB/Poster/1234")]
    // Not a video id: the digits must end the path segment.
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42abc")]
    public void Parse_UnrelatedInput_ReturnsNull(string? url)
        => Assert.Null(StreamUrls.Parse(url));

    [Fact]
    public void TryGetVideoId_ReadsBothShapes()
    {
        Assert.Equal(42, StreamUrls.TryGetVideoId($"{Base}/api/v3/File/42/Stream?apikey=abc"));
        Assert.Equal(42, StreamUrls.TryGetVideoId($"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?sessionId={Theirs}"));
        Assert.Null(StreamUrls.TryGetVideoId("#EXTINF:-1,Title"));
    }

    // ── Entry points, and what the APIv3 fallback may stand in for ──────

    [Theory]
    [InlineData($"{Base}/api/v3/File/42/Stream", true)]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42", true)]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8", true)]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/master-video2.m3u8", true)]
    // #174: every entry point is a master, and no route hands out a media
    // playlist as one — so meeting a bare media playlist where an entry was
    // expected is a surprise, not something to quietly serve a whole file for.
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/media.m3u8", false)]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Transcode/Video/3.m4s", false)]
    [InlineData($"{Base}/api/plugin/MediaSession/v1/Stream/42/Info", false)]
    public void IsEntryPoint_IsTrueOnlyForPlayableEntries(string url, bool expected)
        => Assert.Equal(expected, StreamUrls.Parse(url)!.IsEntryPoint);

    // ── The session-id policy ───────────────────────────────────────────

    [Fact]
    public void ForPlayback_LeavesApiV3Alone()
    {
        var url = $"{Base}/api/v3/File/42/Stream?apikey=abc";

        Assert.Equal(url, StreamUrls.ForPlayback(url, Ours));
        Assert.Equal(url, StreamUrls.ForPlayback(url, null));
    }

    [Fact]
    public void ForPlayback_LeavesUnrecognisedUrlsAlone()
    {
        const string url = "/home/user/Videos/episode.mkv";

        Assert.Equal(url, StreamUrls.ForPlayback(url, Ours));
    }

    [Fact]
    public void ForPlayback_AttachesOurSessionWhenTheUrlHasNone()
    {
        var result = StreamUrls.ForPlayback(
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8", Ours);

        Assert.Equal($"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?sessionId={Ours}", result);
    }

    [Fact]
    public void ForPlayback_ReplacesAForeignSessionWithOurs()
    {
        var result = StreamUrls.ForPlayback(
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?sessionId={Theirs}", Ours);

        Assert.Equal($"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?sessionId={Ours}", result);
        Assert.DoesNotContain(Theirs.ToString(), result);
    }

    [Fact]
    public void ForPlayback_ReplacingKeepsEveryOtherParameterAndItsPosition()
    {
        var result = StreamUrls.ForPlayback(
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/media.m3u8?video=true&sessionId={Theirs}&audioOrdinal=2",
            Ours);

        Assert.Equal(
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/media.m3u8?video=true&sessionId={Ours}&audioOrdinal=2",
            result);
    }

    [Fact]
    public void ForPlayback_IsAlreadyDoneWhenTheSessionIsOurs()
    {
        var url = $"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?sessionId={Ours}";

        Assert.Equal(url, StreamUrls.ForPlayback(url, Ours));
    }

    [Fact]
    public void ForPlayback_MatchesTheSessionParameterCaseInsensitively()
    {
        var result = StreamUrls.ForPlayback(
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?SESSIONID={Theirs}", Ours);

        Assert.DoesNotContain(Theirs.ToString(), result);
        Assert.Contains($"sessionId={Ours}", result);
    }

    // ── The fallback, when we hold no session id of our own ─────────────

    [Fact]
    public void ForPlayback_WithoutOurSession_SwapsAnEntryPointForApiV3()
    {
        var result = StreamUrls.ForPlayback(
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?sessionId={Theirs}", null, "abc");

        Assert.Equal($"{Base}/api/v3/File/42/Stream?apikey=abc", result);
    }

    [Fact]
    public void ForPlayback_WithoutOurSession_SwapsTheBareStreamForApiV3()
    {
        var result = StreamUrls.ForPlayback(
            $"{Base}/api/plugin/MediaSession/v1/Stream/42?sessionId={Theirs}&mode=auto", null, "abc");

        // mode rides along harmlessly; APIv3 ignores what it does not know,
        // and dropping only sessionId keeps the rule simple to state.
        Assert.StartsWith($"{Base}/api/v3/File/42/Stream?", result);
        Assert.DoesNotContain("sessionId", result);
        Assert.Contains("apikey=abc", result);
    }

    [Fact]
    public void ForPlayback_FallbackKeepsThePathBaseAndTheMetadataParameters()
    {
        var result = StreamUrls.ForPlayback(
            $"{Base}/shoko/api/plugin/MediaSession/v1/Stream/42/master.m3u8?sessionId={Theirs}&epNo=3&animeId=7&apikey=abc",
            null);

        Assert.Equal($"{Base}/shoko/api/v3/File/42/Stream?epNo=3&animeId=7&apikey=abc", result);
    }

    [Fact]
    public void ForPlayback_FallbackDoesNotOverwriteAnExistingApiKey()
    {
        var result = StreamUrls.ForPlayback(
            $"{Base}/api/plugin/MediaSession/v1/Stream/42?apikey=fromtheplaylist", null, "fromtheclient");

        Assert.Contains("apikey=fromtheplaylist", result);
        Assert.DoesNotContain("fromtheclient", result);
    }

    [Fact]
    public void ForPlayback_WithoutOurSession_StripsTheSessionFromANonEntryUrl()
    {
        // A segment is not something a whole-file APIv3 stream can stand in
        // for, so the only safe move left is to strip the foreign session and
        // let the endpoint's own guard answer 401.
        var result = StreamUrls.ForPlayback(
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/Transcode/Video/3.m4s?sessionId={Theirs}&videoOrdinal=0",
            null);

        Assert.Equal($"{Base}/api/plugin/MediaSession/v1/Stream/42/Transcode/Video/3.m4s?videoOrdinal=0", result);
        Assert.DoesNotContain("sessionId", result);
    }

    [Fact]
    public void ForPlayback_NeverReturnsAForeignSessionId()
    {
        string[] urls =
        [
            $"{Base}/api/plugin/MediaSession/v1/Stream/42?sessionId={Theirs}",
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/master.m3u8?sessionId={Theirs}",
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/media.m3u8?sessionId={Theirs}",
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/Info?sessionId={Theirs}",
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/Transcode/Audio/9.m4s?sessionId={Theirs}",
            $"{Base}/api/plugin/MediaSession/v1/Stream/42/Remux/Video/init.mp4?sessionId={Theirs}",
        ];

        foreach (var url in urls)
        {
            Assert.DoesNotContain(Theirs.ToString(), StreamUrls.ForPlayback(url, Ours));
            Assert.DoesNotContain(Theirs.ToString(), StreamUrls.ForPlayback(url, null, "abc"));
        }
    }
}
