using Shoko.Companion.Configuration;
using Shoko.Companion.Playback;
using Shoko.Companion.Server;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   What the companion declares to the media session plugin as
///   <c>SessionCapabilities</c>.
///
///   <para>
///     The failure this guards was found live and could not have been
///     found any other way: the declaration and the behaviour were each
///     defensible on their own, and only the server put them in the same
///     room. It refuses a state report from a session that declared it
///     does not report, so a client that says one thing and does the
///     other is not corrected — it is frozen at the last state the server
///     accepted, which for a viewer who stopped playback under privacy
///     mode meant a position and a duration for an item that was no
///     longer playing, held on the wire indefinitely.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class SessionCapabilityDeclarationTests
{
    public SessionCapabilityDeclarationTests()
    {
        var s = SettingsProvider.Instance.Settings;
        s.PrivacyMode = false;
        s.PrivacyModeForRestrictedContent = false;
        s.PrivacyModeDisableRemoteScreenshots = false;
        s.RestrictedContentPlaying = false;
        s.AllowRemotePlay = true;
        s.AllowRemoteScreenshot = true;
        s.AllowRemoteVolumeControl = true;
        s.AllowSessionHandoff = true;
    }

    /// <summary>
    ///   The regression. <c>CanReportState</c> is the one inbound flag —
    ///   it says this client reports, rather than that something may be
    ///   done to it — so it is a property of the build, not of what
    ///   happens to be loaded. Gating it on playback denied exactly the
    ///   two reports that matter most, <c>Stopped</c> and <c>Idle</c>,
    ///   which are the states where by definition nothing is playing.
    /// </summary>
    [Theory]
    [InlineData(PlaybackState.Idle)]
    [InlineData(PlaybackState.Loading)]
    [InlineData(PlaybackState.Playing)]
    [InlineData(PlaybackState.Paused)]
    [InlineData(PlaybackState.Buffering)]
    [InlineData(PlaybackState.Stopped)]
    [InlineData(PlaybackState.Error)]
    public void ReportsState_WhateverIsPlaying(PlaybackState state)
    {
        Assert.True(
            MediaSessionClient.BuildCurrentCapabilities(state).CanReportState);
    }

    /// <summary>
    ///   Privacy governs disclosure and does not govern whether this
    ///   client tells the server the truth about itself. The plugin
    ///   withholds a private item from everybody else; it can only do
    ///   that if it is being told what is playing.
    /// </summary>
    [Fact]
    public void ReportsState_UnderPrivacyMode()
    {
        var s = SettingsProvider.Instance.Settings;
        s.PrivacyMode = true;
        s.PrivacyModeDisableRemoteScreenshots = true;

        Assert.True(MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Idle).CanReportState);
        Assert.True(MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Playing).CanReportState);
    }

    /// <summary>
    ///   Privacy still gates the screenshot pair, which is what makes the
    ///   push cadence matter: toggling privacy moves the declaration, so
    ///   a toggle that does not reach the server leaves the two sides
    ///   disagreeing about what this session can do.
    /// </summary>
    [Fact]
    public void PrivacyMode_Moves_TheScreenshotCapabilities()
    {
        var s = SettingsProvider.Instance.Settings;
        var before = MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Playing);
        Assert.True(before.CanCaptureScreenshot);
        Assert.True(before.CanScreenshotAtPosition);

        s.PrivacyMode = true;
        s.PrivacyModeDisableRemoteScreenshots = true;

        var after = MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Playing);
        Assert.False(after.CanCaptureScreenshot);
        Assert.False(after.CanScreenshotAtPosition);
        Assert.NotEqual(before, after);
    }

    /// <summary>
    ///   The declaration compares by value, which is the whole of what
    ///   lets every caller push freely rather than each working out
    ///   first whether the answer moved.
    /// </summary>
    [Fact]
    public void UnchangedDeclarations_CompareEqual()
    {
        Assert.Equal(
            MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Playing),
            MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Playing));
        Assert.NotEqual(
            MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Playing),
            MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Idle));
    }

    /// <summary>
    ///   A stall is not idleness. The media is loaded, the position is
    ///   real and the last frame is still on screen — only the cache ran
    ///   dry — so every ability that holds while playing holds here too.
    ///   Declaring otherwise made a session go deaf on a slow link at
    ///   exactly the moment somebody reached for the remote.
    /// </summary>
    [Fact]
    public void Buffering_DeclaresEverything_Playing_Does()
    {
        Assert.Equal(
            MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Playing),
            MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Buffering));
    }

    /// <summary>
    ///   Preparing is the other half of the old <c>Loading</c> and it
    ///   goes the other way. Pre-processing a file — building the frame
    ///   index, resolving the source — has produced nothing to act on
    ///   yet, so seeking and capturing a frame stay false rather than
    ///   becoming promises this client cannot keep.
    /// </summary>
    [Fact]
    public void Preparing_DeclaresNoTransport_BecauseNothingIsPlayableYet()
    {
        var preparing = MediaSessionClient.BuildCurrentCapabilities(PlaybackState.Loading);

        Assert.False(preparing.CanSeek);
        Assert.False(preparing.CanResumeOrPause);
        Assert.False(preparing.CanCaptureScreenshot);
        Assert.False(preparing.CanScreenshotAtPosition);
        Assert.False(preparing.CanSelectTracks);
    }

    /// <summary>
    ///   Stopping is the exception, and the reason it is gated on its own
    ///   condition rather than sharing one with the rest.
    ///
    ///   <para>
    ///     It is control of the session's attention, not of playback: a
    ///     file still being pre-processed cannot be sought, but it can be
    ///     abandoned, and a viewer who started the wrong one should not
    ///     have to wait out a frame-index build to say so. Idle is still
    ///     false — there is nothing there to abandon.
    ///   </para>
    /// </summary>
    [Theory]
    [InlineData(PlaybackState.Loading, true)]
    [InlineData(PlaybackState.Playing, true)]
    [InlineData(PlaybackState.Paused, true)]
    [InlineData(PlaybackState.Buffering, true)]
    [InlineData(PlaybackState.Idle, false)]
    [InlineData(PlaybackState.Stopped, false)]
    [InlineData(PlaybackState.Error, false)]
    public void CanStop_IsGated_MoreLoosely_ThanTheRest(PlaybackState state, bool expected)
    {
        var declared = MediaSessionClient.BuildCurrentCapabilities(state);

        Assert.Equal(expected, declared.CanStop);

        // And the loosening is CanStop's alone: the seam only exists
        // where the two conditions actually differ.
        if (state is PlaybackState.Loading)
            Assert.False(declared.CanSeek);
    }
}
