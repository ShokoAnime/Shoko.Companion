using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json.Linq;
using Shoko.Companion.Configuration;
using Shoko.Companion.Discord;
using Shoko.Companion.Mpv;
using Shoko.Companion.Notifications;
using Shoko.Companion.Playback;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   Holds the position this companion reports around the end of a file to
///   the last one mpv actually measured.
///
///   <para>
///     When mpv unloads a file at its end, <c>time-pos</c> becomes
///     unavailable and mpv says so with <c>"data": null</c>. That used to be
///     read as position 0, so the last tick of a finished episode reported
///     "playing, at 0:00", the plugin's end-of-playback save was made from
///     it, and a 23:40 episode watched to the end was stored as watched for
///     5 ms and never marked watched. The <c>Stopped</c> report said 0 too,
///     because it is built after the session object is gone.
///   </para>
///   <para>
///     And a file that played to its end is reported <em>at</em> its end.
///     The last real position is the last frame's timestamp, up to a frame
///     short of the duration, and at a 100% completion threshold on the
///     server that is a file watched to the very end and never marked
///     watched. So on a real end of file the position becomes the
///     duration, never lower than what was measured, and only when the
///     duration is known.
///   </para>
///   <para>
///     Every failure here is silent in production: nothing errors, the
///     watch state is just wrong.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class EndOfFilePositionTests : IDisposable
{
    private const int VideoId = 29529;
    private const double DurationMs = 1_420_000; // 23:40
    private const double LastRealSeconds = 1_419.9;

    private readonly bool _syncingEnabled;
    private readonly bool _discordEnabled;
    private readonly int _skipCount;
    private readonly bool _privacyMode;

    private readonly Mock<IMpvController> _mpv = new();
    private readonly PlaybackSessionManager _manager = new();
    private readonly PlaybackCoordinator _coordinator;
    private readonly List<(PlaybackState State, double PositionSeconds)> _reports = [];

    public EndOfFilePositionTests()
    {
        var s = SettingsProvider.Instance.Settings;
        _syncingEnabled = s.PlaybackSyncingEnabled;
        _discordEnabled = s.DiscordEnabled;
        _skipCount = s.SyncUserDataInitialSkipEventCount;
        _privacyMode = s.PrivacyMode;
        s.DiscordEnabled = false;
        // The bug's own setting: the server owns the viewing, so the
        // companion's reports are the only account of it.
        _manager.MediaSessionConnected = true;

        _coordinator = new PlaybackCoordinator(
            Mock.Of<IShokoApiClient>(), _mpv.Object, Mock.Of<INotificationService>(),
            Mock.Of<IDiscordPresenceService>(), _manager);

        // Read the way every report call site in App reads it.
        _coordinator.StateChanged += (_, e)
            => _reports.Add((e.NewState, _coordinator.CurrentPositionSeconds));
    }

    public void Dispose()
    {
        _manager.EndSession(_manager.CurrentPositionMs);
        var s = SettingsProvider.Instance.Settings;
        s.PlaybackSyncingEnabled = _syncingEnabled;
        s.DiscordEnabled = _discordEnabled;
        s.SyncUserDataInitialSkipEventCount = _skipCount;
        s.PrivacyMode = _privacyMode;
        s.RestrictedContentPlaying = false;
        GC.SuppressFinalize(this);
    }

    private void Property(string name, object? data)
        => _mpv.Raise(m => m.PropertyChanged += null, new MpvPropertyChangeEventArgs(1, name, data));

    private void Event(string name, object? data = null)
        => _mpv.Raise(m => m.MpvEvent += null, new MpvEventArgs(name, data));

    /// <summary>The payload as <c>MpvIpcClient</c> hands it on.</summary>
    private static object? Wire(string json)
        => JObject.Parse(json)["data"]!.ToObject<object>();

    private static string StreamUrl(int videoId) => $"http://shoko.test/api/v3/File/{videoId}/Stream";

    /// <summary>mpv's <c>playlist</c> property: two files, one current.</summary>
    private static JArray Playlist(int currentVideoId)
        => new(
            new JObject { ["filename"] = StreamUrl(VideoId), ["title"] = "Episode 1", ["current"] = currentVideoId == VideoId },
            new JObject { ["filename"] = StreamUrl(VideoId + 1), ["title"] = "Episode 2", ["current"] = currentVideoId == VideoId + 1 });

    /// <summary>A session playing video 29529, as the coordinator sees it.</summary>
    private void StartPlaying(double durationMs = DurationMs)
    {
        _manager.StartSession(VideoId, 0, durationMs, false, "Series", "Episode", 1, 1, 12, null, 1);
        Property("time-pos", LastRealSeconds);
        Property("pause", false);
        Assert.Equal(PlaybackState.Playing, _coordinator.CurrentState);
        _reports.Clear();
    }

    private void SetDuration(double ms)
    {
        var field = typeof(PlaybackCoordinator).GetField("_duration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field); // renamed: this test has to follow it
        field.SetValue(_coordinator, ms);
    }

    [Fact]
    public void TimePos_NullOnTheWire_IsNoPosition()
    {
        Assert.Null(PlaybackCoordinator.TimePosMs(Wire("""{"data":null}""")));
        Assert.Null(PlaybackCoordinator.TimePosMs(null));
        Assert.Null(PlaybackCoordinator.TimePosMs("no"));
    }

    [Fact]
    public void TimePos_ZeroOnTheWire_IsPositionZero()
    {
        Assert.Equal(0, PlaybackCoordinator.TimePosMs(Wire("""{"data":0.000000}""")));
        Assert.Equal(0, PlaybackCoordinator.TimePosMs(Wire("""{"data":0}""")));
        Assert.Equal(1_370_500, PlaybackCoordinator.TimePosMs(Wire("""{"data":1370.5}""")));
    }

    [Fact]
    public void NullTimePos_LeavesThePositionUnchanged()
    {
        StartPlaying();

        Property("time-pos", null);

        Assert.Equal(LastRealSeconds * 1000, _manager.CurrentPositionMs, 3);
        Assert.Equal(LastRealSeconds, _coordinator.CurrentPositionSeconds, 3);
    }

    [Fact]
    public void NumericZeroTimePos_StillSetsZero()
    {
        StartPlaying();

        Property("time-pos", 0.0);
        Assert.Equal(0, _manager.CurrentPositionMs);
        Assert.Equal(0, _coordinator.CurrentPositionSeconds);

        Property("time-pos", LastRealSeconds);
        Property("time-pos", 0L);
        Assert.Equal(0, _coordinator.CurrentPositionSeconds);
    }

    [Fact]
    public async Task TickAfterTheFileUnloads_CarriesTheLastRealPosition()
    {
        StartPlaying();
        var tick = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.PositionTick += (_, e) => tick.TrySetResult(e.Position);

        Property("time-pos", null);
        _manager.OnScrobbleTimer(null);

        var reported = await tick.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackState.Playing, _coordinator.CurrentState);
        Assert.Equal(LastRealSeconds, reported.TotalSeconds, 3);
    }

    [Fact]
    public async Task TickAfterTheSessionEnded_IsNotSent()
    {
        StartPlaying();
        var tick = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.PositionTick += (_, e) => tick.TrySetResult(e.Position);

        _manager.EndSession(_manager.CurrentPositionMs);
        _manager.OnScrobbleTimer(null);

        await Task.Delay(500);
        Assert.False(tick.Task.IsCompleted);
    }

    [Fact]
    public void EndOfFile_StoppedReportCarriesTheDuration()
    {
        StartPlaying();

        // What mpv sends as a file finishes: the position goes away, then
        // end-file says why.
        Property("time-pos", null);
        Event("end-file", JObject.Parse("""{"reason":"eof"}"""));

        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(DurationMs / 1000, stopped.PositionSeconds, 3);
        Assert.Equal(DurationMs, _manager.LastKnownPositionMs);
        Assert.Equal(VideoId, _manager.PositionVideoId);
        Assert.DoesNotContain(_reports, r => r.PositionSeconds == 0);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("quit")]
    [InlineData("error")]
    [InlineData("redirect")]
    public async Task EndThatIsNotEof_KeepsTheLastRealPosition(string reason)
    {
        StartPlaying();

        Event("end-file", new JObject { ["reason"] = reason });
        Assert.Equal(LastRealSeconds, _coordinator.CurrentPositionSeconds, 3);
        await _coordinator.StopAsync();

        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(LastRealSeconds, stopped.PositionSeconds, 3);
        Assert.Equal(LastRealSeconds * 1000, _manager.LastKnownPositionMs!.Value, 3);
    }

    [Fact]
    public void EndOfFile_DurationUnknown_KeepsTheLastRealPosition()
    {
        StartPlaying(durationMs: 0);

        Property("time-pos", null);
        Event("end-file", JObject.Parse("""{"reason":"eof"}"""));

        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(LastRealSeconds, stopped.PositionSeconds, 3);
    }

    [Fact]
    public void EndOfFile_PastTheDuration_IsNotLowered()
    {
        StartPlaying();
        // mpv's last frame can sit past what the server has for the file.
        Property("time-pos", 1_420.5);

        Event("end-file", JObject.Parse("""{"reason":"eof"}"""));

        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(1_420.5, stopped.PositionSeconds, 3);
        Assert.Equal(1_420_500, _manager.LastKnownPositionMs!.Value, 3);
    }

    [Fact]
    public async Task PlaylistAdvanceAfterEof_DoesNotCarryTheDurationOntoTheNextItem()
    {
        Property("playlist", Playlist(VideoId));
        StartPlaying();

        Event("end-file", JObject.Parse("""{"reason":"eof"}"""));

        // More is queued, so the session carries on; the finished file is
        // at its end for as long as a report names it.
        Assert.NotEqual(PlaybackState.Stopped, _coordinator.CurrentState);
        Assert.Equal(DurationMs / 1000, _coordinator.CurrentPositionSeconds, 3);

        // mpv moves its playlist on before the next file's first time-pos.
        _reports.Clear();
        Property("playlist", Playlist(VideoId + 1));
        Assert.Equal(VideoId + 1, _coordinator.CurrentItem?.VideoId);
        var moved = Assert.Single(_reports);
        Assert.Equal(0, moved.PositionSeconds);

        // And once the next file is loaded, its position is its own.
        Property("path", StreamUrl(VideoId + 1));
        Assert.Equal(VideoId + 1, _manager.PositionVideoId);
        Assert.Equal(0, _coordinator.CurrentPositionSeconds);

        var tick = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.PositionTick += (_, e) => tick.TrySetResult(e.Position);
        _manager.OnScrobbleTimer(null);
        Assert.Equal(TimeSpan.Zero, await tick.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Stop_ReportCarriesTheLastRealPosition()
    {
        StartPlaying();

        await _coordinator.StopAsync();

        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(LastRealSeconds, stopped.PositionSeconds, 3);
    }

    [Fact]
    public void PauseAtTheEnd_AfterTheFileUnloads_IsStillDetected()
    {
        StartPlaying();
        SetDuration(DurationMs);

        // Before the fix the null zeroed the position and this branch, which
        // asks for a position within 300 ms of the end, could not fire.
        Property("time-pos", null);
        Property("pause", true);

        // Under --keep-open this pause is the end of the last file: mpv
        // sends no end-file for it. So it ends at the duration too.
        Assert.Equal(PlaybackState.Stopped, _coordinator.CurrentState);
        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(DurationMs / 1000, stopped.PositionSeconds, 3);
    }

    [Fact]
    public void PauseAtTheEnd_AfterEofReached_IsStillDetected()
    {
        StartPlaying();

        Property("eof-reached", true);
        Property("time-pos", null);
        Property("pause", true);

        Assert.Equal(PlaybackState.Stopped, _coordinator.CurrentState);
        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(DurationMs / 1000, stopped.PositionSeconds, 3);
    }

    [Fact]
    public void PauseMidFile_IsAPause()
    {
        StartPlaying();
        SetDuration(DurationMs);
        Property("time-pos", 600.0);

        Property("pause", true);

        Assert.Equal(PlaybackState.Paused, _coordinator.CurrentState);
    }

    [Fact]
    public void NewSession_DropsTheEndedPosition()
    {
        StartPlaying();
        _manager.EndSession(_manager.CurrentPositionMs);
        Assert.Equal(LastRealSeconds * 1000, _manager.LastKnownPositionMs!.Value, 3);

        _manager.StartSession(VideoId + 1, 5_000, DurationMs, false, "Series", "Episode", 2, 1, 12, null, 1);

        Assert.Equal(VideoId + 1, _manager.PositionVideoId);
        Assert.Equal(5_000, _manager.LastKnownPositionMs);
    }

    /// <summary>
    ///   A manager with no media session, so the companion scrobbles the
    ///   viewing itself, and the <c>PlaybackEnd</c> it would send.
    /// </summary>
    private static PlaybackSessionManager OwnScrobbling(TaskCompletionSource<ScrobbleRequestEventArgs> end, double durationMs = DurationMs)
    {
        var s = SettingsProvider.Instance.Settings;
        s.PlaybackSyncingEnabled = true;
        s.PrivacyMode = false;
        s.SyncUserDataInitialSkipEventCount = 0;

        var manager = new PlaybackSessionManager();
        manager.ScrobbleRequested += (_, e) =>
        {
            if (e.EventType is ScrobbleEventType.PlaybackEnd)
                end.TrySetResult(e);
        };
        manager.StartSession(VideoId, 0, durationMs, false, "Series", "Episode", 1, 1, 12, null, 1);
        manager.OnPositionChanged(LastRealSeconds * 1000);
        return manager;
    }

    [Fact]
    public async Task OwnScrobble_FileFinalizedAtEof_EndsAtTheDuration()
    {
        var end = new TaskCompletionSource<ScrobbleRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = OwnScrobbling(end);

        manager.OnEofReached();
        manager.FinalizeCurrentFile();

        var sent = await end.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(DurationMs), sent.Position);
        Assert.True(sent.IsWatched);
        manager.EndSession(manager.CurrentPositionMs);
    }

    [Fact]
    public async Task OwnScrobble_SessionEndedAtEof_EndsAtTheDuration()
    {
        var end = new TaskCompletionSource<ScrobbleRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = OwnScrobbling(end);

        manager.OnEofReached();
        manager.EndSession(manager.CurrentPositionMs);

        var sent = await end.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(DurationMs), sent.Position);
        Assert.True(sent.IsWatched);
        Assert.Equal(DurationMs, manager.LastKnownPositionMs);
    }

    [Fact]
    public async Task OwnScrobble_SessionEndedWithoutEof_EndsAtTheLastRealPosition()
    {
        var end = new TaskCompletionSource<ScrobbleRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = OwnScrobbling(end);

        manager.EndSession(manager.CurrentPositionMs);

        var sent = await end.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LastRealSeconds * 1000, sent.Position!.Value.TotalMilliseconds, 3);
    }

    [Fact]
    public async Task OwnScrobble_EofWithTheDurationUnknown_EndsAtTheLastRealPosition()
    {
        var end = new TaskCompletionSource<ScrobbleRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = OwnScrobbling(end, durationMs: 0);

        manager.OnEofReached();
        manager.EndSession(manager.CurrentPositionMs);

        var sent = await end.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LastRealSeconds * 1000, sent.Position!.Value.TotalMilliseconds, 3);
        Assert.True(sent.IsWatched);
    }

    [Theory]
    [InlineData(null, VideoId, 1_000.0, 1_000.0)]    // no item named: the last position stands
    [InlineData(VideoId, VideoId, 1_000.0, 1_000.0)] // the item it was measured on
    [InlineData(VideoId + 1, VideoId, 1_000.0, 0.0)] // mpv moved on before the next file's first time-pos
    [InlineData(null, null, null, 0.0)]              // nothing ever measured
    public void ReportablePosition_BelongsToTheItemItWasMeasuredOn(
        int? itemVideoId, int? positionVideoId, double? positionMs, double expected)
        => Assert.Equal(expected, PlaybackCoordinator.ReportablePositionMs(itemVideoId, positionVideoId, positionMs));
}
