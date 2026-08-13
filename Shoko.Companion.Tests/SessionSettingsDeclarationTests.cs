using Shoko.Companion.Configuration;
using Shoko.Companion.Server;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   What the companion declares to the media session plugin as
///   <c>SessionSettings</c>.
///
///   <para>
///     Worth a test of its own because the failure it guards is silent by
///     construction. Every field here defaults to <c>false</c> on the
///     server, so a mapping that is wrong — or, as it was until this
///     wiring existed, absent — produces no error anywhere: the server
///     applies its defaults, the viewer's switch reads on locally, and the
///     only visible symptom is watch state quietly being written for
///     something the viewer asked to keep private.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class SessionSettingsDeclarationTests
{
    public SessionSettingsDeclarationTests()
    {
        var s = SettingsProvider.Instance.Settings;
        s.PrivacyMode = false;
        s.PrivacyModeForRestrictedContent = false;
        s.PrivacyModeDisablePlaybackEvents = false;
        s.RestrictedContentPlaying = false;
    }

    [Fact]
    public void Declares_NothingOn_ByDefault()
    {
        var declared = MediaSessionClient.BuildCurrentSettings();

        Assert.False(declared.PrivacyModeEnabled);
        Assert.False(declared.AlwaysUsePrivacyModeForRestrictedContent);
        Assert.False(declared.DisablePlaybackEventSyncing);
    }

    [Fact]
    public void PrivacyMode_Drives_PrivacyModeEnabled()
    {
        SettingsProvider.Instance.Settings.PrivacyMode = true;

        Assert.True(MediaSessionClient.BuildCurrentSettings().PrivacyModeEnabled);
    }

    [Fact]
    public void RestrictedContentSetting_Drives_ItsOwnField_AndNotTheMaster()
    {
        SettingsProvider.Instance.Settings.PrivacyModeForRestrictedContent = true;

        var declared = MediaSessionClient.BuildCurrentSettings();

        Assert.True(declared.AlwaysUsePrivacyModeForRestrictedContent);
        Assert.False(declared.PrivacyModeEnabled);
    }

    /// <summary>
    ///   The one that would be easy to get wrong, and expensive.
    ///
    ///   <para>
    ///     <see cref="CompanionSettings.EffectivePrivacyMode"/> turns on by
    ///     itself when restricted content starts playing, and declaring
    ///     <em>that</em> as the master switch would be the obvious reading
    ///     of "privacy is on". It is wrong, because the two fields do not
    ///     have the same reach on the far side: the server's master switch
    ///     re-resolves the whole queue when it transitions on, while its
    ///     restricted rule deliberately never reaches back into the queue.
    ///     Sending the effective value would make one restricted episode
    ///     retroactively privatise everything already queued, which the
    ///     ratchet then makes permanent.
    ///   </para>
    /// </summary>
    [Fact]
    public void RestrictedContentPlaying_DoesNotRaise_TheMasterSwitch()
    {
        var s = SettingsProvider.Instance.Settings;
        s.PrivacyModeForRestrictedContent = true;
        s.RestrictedContentPlaying = true;

        Assert.True(s.EffectivePrivacyMode);
        Assert.False(MediaSessionClient.BuildCurrentSettings().PrivacyModeEnabled);
    }

    [Fact]
    public void PlaybackEventSetting_Drives_DisablePlaybackEventSyncing()
    {
        SettingsProvider.Instance.Settings.PrivacyModeDisablePlaybackEvents = true;

        Assert.True(MediaSessionClient.BuildCurrentSettings().DisablePlaybackEventSyncing);
    }
}
