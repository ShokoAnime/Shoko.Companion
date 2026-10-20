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

    private readonly Mock<IMpvController> _mpv = new();
    private readonly PlaybackSessionManager _manager = new();
    private readonly PlaybackCoordinator _coordinator;
    private readonly List<(PlaybackState State, double PositionSeconds)> _reports = [];

    public EndOfFilePositionTests()
    {
        var s = SettingsProvider.Instance.Settings;
        _syncingEnabled = s.PlaybackSyncingEnabled;
        _discordEnabled = s.DiscordEnabled;
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

    /// <summary>A session playing video 29529, as the coordinator sees it.</summary>
    private void StartPlaying()
    {
        _manager.StartSession(VideoId, 0, DurationMs, false, "Series", "Episode", 1, 1, 12, null, 1);
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
        _coordinator.PositionTick += (_, pos) => tick.TrySetResult(pos);

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
        _coordinator.PositionTick += (_, pos) => tick.TrySetResult(pos);

        _manager.EndSession(_manager.CurrentPositionMs);
        _manager.OnScrobbleTimer(null);

        await Task.Delay(500);
        Assert.False(tick.Task.IsCompleted);
    }

    [Fact]
    public void EndOfFile_StoppedReportCarriesTheLastRealPosition()
    {
        StartPlaying();

        // What mpv sends as a file finishes: the position goes away, then
        // end-file says why.
        Property("time-pos", null);
        Event("end-file", JObject.Parse("""{"reason":"eof"}"""));

        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(LastRealSeconds, stopped.PositionSeconds, 3);
        Assert.DoesNotContain(_reports, r => r.PositionSeconds == 0);
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

        Assert.Equal(PlaybackState.Stopped, _coordinator.CurrentState);
        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(LastRealSeconds, stopped.PositionSeconds, 3);
    }

    [Fact]
    public void PauseAtTheEnd_AfterEofReached_IsStillDetected()
    {
        StartPlaying();

        Property("eof-reached", true);
        Property("time-pos", null);
        Property("pause", true);

        Assert.Equal(PlaybackState.Stopped, _coordinator.CurrentState);
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

    [Theory]
    [InlineData(null, VideoId, 1_000.0, 1_000.0)]    // no item named: the last position stands
    [InlineData(VideoId, VideoId, 1_000.0, 1_000.0)] // the item it was measured on
    [InlineData(VideoId + 1, VideoId, 1_000.0, 0.0)] // mpv moved on before the next file's first time-pos
    [InlineData(null, null, null, 0.0)]              // nothing ever measured
    public void ReportablePosition_BelongsToTheItemItWasMeasuredOn(
        int? itemVideoId, int? positionVideoId, double? positionMs, double expected)
        => Assert.Equal(expected, PlaybackCoordinator.ReportablePositionMs(itemVideoId, positionVideoId, positionMs));
}
