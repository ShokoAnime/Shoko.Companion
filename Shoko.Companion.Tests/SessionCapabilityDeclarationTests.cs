using Shoko.Companion.Configuration;
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
    [InlineData(true)]
    [InlineData(false)]
    public void ReportsState_WhetherOrNotAnythingIsPlaying(bool hasActivePlayback)
    {
        Assert.True(
            MediaSessionClient.BuildCurrentCapabilities(hasActivePlayback).CanReportState);
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

        Assert.True(MediaSessionClient.BuildCurrentCapabilities(false).CanReportState);
        Assert.True(MediaSessionClient.BuildCurrentCapabilities(true).CanReportState);
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
        var before = MediaSessionClient.BuildCurrentCapabilities(true);
        Assert.True(before.CanCaptureScreenshot);
        Assert.True(before.CanScreenshotAtPosition);

        s.PrivacyMode = true;
        s.PrivacyModeDisableRemoteScreenshots = true;

        var after = MediaSessionClient.BuildCurrentCapabilities(true);
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
            MediaSessionClient.BuildCurrentCapabilities(true),
            MediaSessionClient.BuildCurrentCapabilities(true));
        Assert.NotEqual(
            MediaSessionClient.BuildCurrentCapabilities(true),
            MediaSessionClient.BuildCurrentCapabilities(false));
    }
}
