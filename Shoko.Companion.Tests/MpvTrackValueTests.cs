using Shoko.Companion.Playback;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
/// The media session's three-valued track fields, translated to mpv's
/// <c>vid</c> / <c>aid</c> / <c>sid</c> values and back.
///
/// Worth its own file because every failure here is silent. A sentinel
/// read as a track number picks the wrong track, mpv reports it back as
/// though a viewer had chosen it, and the ordinal is then persisted to
/// Shoko and handed to the next client as a preference nobody set.
/// </summary>
public class MpvTrackValueTests
{
    [Fact]
    public void NothingStatedWritesNothing()
    {
        // A state report carries all three fields and is usually talking
        // about one of them, so an absent field has to leave the other
        // kinds alone rather than reset them.
        Assert.Null(MpvTrackValue.Resolve(null, trackCount: 3, isSubtitle: false));
        Assert.Null(MpvTrackValue.Resolve(null, trackCount: 3, isSubtitle: true));
    }

    [Fact]
    public void AnOrdinalIsMpvsIndexPlusOne()
    {
        Assert.Equal(1, MpvTrackValue.Resolve(0, trackCount: 3, isSubtitle: false));
        Assert.Equal(3, MpvTrackValue.Resolve(2, trackCount: 3, isSubtitle: false));
    }

    [Fact]
    public void ClearAsksMpvToChooseForItself()
    {
        // -2 is a verb, not a track: "back to whatever this file marks
        // default", which is exactly mpv's "auto".
        Assert.Equal("auto",
            MpvTrackValue.Resolve(MpvTrackValue.Clear, trackCount: 3, isSubtitle: false));
        Assert.Equal("auto",
            MpvTrackValue.Resolve(MpvTrackValue.Clear, trackCount: 3, isSubtitle: true));
    }

    [Fact]
    public void SubtitlesOffIsAStateAndOnlySubtitlesHaveIt()
    {
        Assert.Equal("no",
            MpvTrackValue.Resolve(MpvTrackValue.Off, trackCount: 3, isSubtitle: true));

        // -1 on video or audio is out-of-bounds noise. Turning the video
        // track off is not a thing anyone asked for, and taking it as one
        // would leave a black window.
        Assert.Null(
            MpvTrackValue.Resolve(MpvTrackValue.Off, trackCount: 3, isSubtitle: false));
    }

    [Fact]
    public void AnOrdinalThisFileHasNoTrackForWritesNothing()
    {
        // Not an error. The ordinals describe whatever file the sender was
        // looking at, which may be a different release of the same episode
        // with fewer tracks; the file's own default is the honest answer.
        Assert.Null(MpvTrackValue.Resolve(5, trackCount: 3, isSubtitle: false));
        Assert.Null(MpvTrackValue.Resolve(0, trackCount: 0, isSubtitle: true));
    }

    [Fact]
    public void MpvsIndexReadsBackAsTheOrdinalBelowIt()
    {
        Assert.Equal(0, MpvTrackValue.ToOrdinal(1, trackCount: 3));
        Assert.Equal(2, MpvTrackValue.ToOrdinal(3, trackCount: 3));
    }

    [Fact]
    public void ADisabledOrUnknownTrackReadsBackAsNoSelection()
    {
        // mpv reports "no" as a null id when the track is off, and can
        // name an id past the end of what the playlist entry described.
        Assert.Null(MpvTrackValue.ToOrdinal(null, trackCount: 3));
        Assert.Null(MpvTrackValue.ToOrdinal(0, trackCount: 3));
        Assert.Null(MpvTrackValue.ToOrdinal(4, trackCount: 3));
    }

    [Fact]
    public void TheTwoDirectionsAgreeOnEveryRealTrack()
    {
        for (var ordinal = 0; ordinal < 4; ordinal++)
        {
            var mpvValue = MpvTrackValue.Resolve(
                ordinal, trackCount: 4, isSubtitle: false);
            Assert.Equal(ordinal, MpvTrackValue.ToOrdinal((int?)mpvValue, trackCount: 4));
        }
    }
}
