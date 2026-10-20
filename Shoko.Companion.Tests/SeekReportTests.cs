using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using Moq;
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
///   A seek is reported, once, on the first position after mpv lands it.
///
///   <para>
///     The server used to guess seeks from position jumps and now relies
///     on this signal: once a viewer is past its watch-sync gate it saves
///     the watch position on a report that says <c>Seeked</c>, instead of
///     at its next 60 s interval. A missed seek costs up to a minute of
///     watch position; a replayed one, from a baseline carried into a
///     reconnect, is a seek that never happened.
///   </para>
///   <para>
///     Two halves. The coordinator marks exactly one tick per seek, the
///     one built from mpv's first real <c>time-pos</c> after the seek,
///     whoever asked for it. And the client sends <c>Seeked</c> on that
///     report alone: never in a baseline, never on the report after it.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class SeekReportTests : IDisposable
{
    private const int VideoId = 29529;
    private const double DurationMs = 1_420_000; // 23:40

    private readonly bool _syncingEnabled;
    private readonly bool _discordEnabled;
    private readonly bool _privacyMode;

    private readonly Mock<IMpvController> _mpv = new();
    private readonly PlaybackSessionManager _manager = new();
    private readonly PlaybackCoordinator _coordinator;
    private readonly ConcurrentQueue<PositionTickEventArgs> _ticks = new();

    public SeekReportTests()
    {
        var s = SettingsProvider.Instance.Settings;
        _syncingEnabled = s.PlaybackSyncingEnabled;
        _discordEnabled = s.DiscordEnabled;
        _privacyMode = s.PrivacyMode;
        s.DiscordEnabled = false;
        _manager.MediaSessionConnected = true;

        _coordinator = new PlaybackCoordinator(
            Mock.Of<IShokoApiClient>(), _mpv.Object, Mock.Of<INotificationService>(),
            Mock.Of<IDiscordPresenceService>(), _manager);

        _coordinator.PositionTick += (_, e) => _ticks.Enqueue(e);
    }

    public void Dispose()
    {
        _manager.EndSession(_manager.CurrentPositionMs);
        var s = SettingsProvider.Instance.Settings;
        s.PlaybackSyncingEnabled = _syncingEnabled;
        s.DiscordEnabled = _discordEnabled;
        s.PrivacyMode = _privacyMode;
        s.RestrictedContentPlaying = false;
        GC.SuppressFinalize(this);
    }

    private void Property(string name, object? data)
        => _mpv.Raise(m => m.PropertyChanged += null, new MpvPropertyChangeEventArgs(1, name, data));

    private void Event(string name, object? data = null)
        => _mpv.Raise(m => m.MpvEvent += null, new MpvEventArgs(name, data));

    private void StartSession()
        => _manager.StartSession(VideoId, 0, DurationMs, false, "Series", "Episode", 1, 1, 12, null, 1);

    private void StartPlaying()
    {
        StartSession();
        Property("time-pos", 60.0);
        Property("pause", false);
        Assert.Equal(PlaybackState.Playing, _coordinator.CurrentState);
    }

    /// <summary>What mpv sends for a seek, up to the position it landed at.</summary>
    private void MpvSeeks(double landedSeconds)
    {
        Event("seek");
        Event("playback-restart");
        Property("time-pos", landedSeconds);
    }

    /// <summary>
    ///   Ticks are raised on the thread pool, so wait for the one that says
    ///   a seek, then a beat longer for anything that should not be there.
    /// </summary>
    private async Task<PositionTickEventArgs[]> TicksAfterASeekAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_ticks.Any(t => t.Seeked) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(200);
        return [.. _ticks];
    }

    private static string WriteInvocation(object? payload)
    {
        var options = new NewtonsoftJsonHubProtocolOptions();
        MediaSessionClient.ConfigureHubPayload(options);

        var buffer = new ArrayBufferWriter<byte>();
        new NewtonsoftJsonHubProtocol(Options.Create(options))
            .WriteMessage(new InvocationMessage("UpdateState", [payload]), buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static PlaybackStateUpdateDto Report(TimeSpan position, bool? seeked = null)
        => new()
        {
            State = "playing",
            CurrentItem = new MediaItemInfoDto { Title = "Episode 1", VideoId = 7 },
            Position = position,
            Duration = TimeSpan.FromMinutes(24),
            IsPaused = false,
            Volume = 83,
            Seeked = seeked,
        };

    private static PlaybackStateUpdateDto? LastState(MediaSessionClient client)
    {
        var field = typeof(MediaSessionClient).GetField("_lastState", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field); // renamed: this test has to follow it
        return (PlaybackStateUpdateDto?)field.GetValue(client);
    }

    [Fact]
    public async Task AnMpvSeek_MarksExactlyOneTick_AtWhereItLanded()
    {
        StartPlaying();

        MpvSeeks(600.0);

        var ticks = await TicksAfterASeekAsync();
        var seek = Assert.Single(ticks, t => t.Seeked);
        Assert.Equal(600.0, seek.Position.TotalSeconds, 3);
    }

    [Fact]
    public async Task TheTicksAfterASeek_DoNotCarryIt()
    {
        StartPlaying();
        MpvSeeks(600.0);
        await TicksAfterASeekAsync();

        // Playback carries on: a position change is not a seek, and the
        // regular beat is not either.
        Property("time-pos", 601.0);
        _manager.OnScrobbleTimer(null);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_ticks.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(200);

        Assert.Equal(2, _ticks.Count);
        Assert.Single(_ticks, t => t.Seeked);
        Assert.Contains(_ticks, t => !t.Seeked && Math.Abs(t.Position.TotalSeconds - 601.0) < 0.001);
    }

    [Fact]
    public async Task ACommandedSeek_IsReportedOnceMpvLands()
    {
        StartPlaying();

        await _coordinator.SeekAsync(TimeSpan.FromSeconds(900));

        // Asked for, not landed: the session moves, the hub hears the
        // position, and nothing says seeked yet.
        await Task.Delay(200);
        Assert.DoesNotContain(_ticks, t => t.Seeked);

        MpvSeeks(900.0);

        var ticks = await TicksAfterASeekAsync();
        var seek = Assert.Single(ticks, t => t.Seeked);
        Assert.Equal(900.0, seek.Position.TotalSeconds, 3);
    }

    [Fact]
    public async Task ANullTimePosDuringASeek_IsNeverASeekAtZero()
    {
        StartPlaying();

        Event("seek");
        Property("time-pos", null);
        Event("playback-restart");
        Property("time-pos", null);

        await Task.Delay(200);
        Assert.Empty(_ticks);
        Assert.Equal(60.0, _coordinator.CurrentPositionSeconds, 3);

        // The seek stays pending for the report that has a number.
        Property("time-pos", 600.0);

        var ticks = await TicksAfterASeekAsync();
        var seek = Assert.Single(ticks, t => t.Seeked);
        Assert.Equal(600.0, seek.Position.TotalSeconds, 3);
        Assert.DoesNotContain(ticks, t => t.Position == TimeSpan.Zero);
    }

    /// <summary>
    ///   The resume seek is issued at <c>file-loaded</c> before the session
    ///   is started, and mpv can land it first. It is not lost: it marks the
    ///   first position once the session exists.
    /// </summary>
    [Fact]
    public async Task AResumeSeekThatLandsBeforeTheSession_IsReportedOnceItStarts()
    {
        MpvSeeks(300.0);

        await Task.Delay(200);
        Assert.Empty(_ticks);

        StartSession();
        Property("time-pos", 300.5);

        var ticks = await TicksAfterASeekAsync();
        var seek = Assert.Single(ticks, t => t.Seeked);
        Assert.Equal(300.5, seek.Position.TotalSeconds, 3);
    }

    [Fact]
    public void SeekedIsAbsentWhenNull_AndTrueWhenSaid()
    {
        Assert.DoesNotContain("Seeked", WriteInvocation(Report(TimeSpan.FromMinutes(1))));
        Assert.DoesNotContain("Seeked", WriteInvocation(new PlaybackStateUpdateDto()));
        Assert.Contains("\"Seeked\":true", WriteInvocation(Report(TimeSpan.FromMinutes(1), seeked: true)));
    }

    /// <summary>
    ///   On the wire, through the patch: the seek report says the seek and
    ///   where it landed, and the report after it says neither.
    /// </summary>
    [Fact]
    public async Task ASeekReportIsSentOnce_WithItsPosition()
    {
        await using var client = new MediaSessionClient(
            "http://localhost:8111", "apikey", "device", new StubPlaybackCoordinator());
        client.SetSessionId(Guid.NewGuid());
        client.PatchToSend(Report(TimeSpan.FromMinutes(1)));

        var seek = WriteInvocation(client.PatchToSend(Report(TimeSpan.FromMinutes(10), seeked: true)));
        Assert.Contains("\"arguments\":[{\"Position\":\"00:10:00\",\"Seeked\":true}]", seek);

        var next = WriteInvocation(client.PatchToSend(Report(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(10)))));
        Assert.Contains("\"arguments\":[{\"Position\":\"00:10:10\"}]", next);
    }

    /// <summary>
    ///   A commanded seek moves the session to where it was asked to go
    ///   before mpv lands, so the baseline may already hold the landed
    ///   position. The seek report still says where.
    /// </summary>
    [Fact]
    public async Task ASeekToAPositionAlreadyReported_StillGoesOut_WithThePosition()
    {
        await using var client = new MediaSessionClient(
            "http://localhost:8111", "apikey", "device", new StubPlaybackCoordinator());
        client.SetSessionId(Guid.NewGuid());
        client.PatchToSend(Report(TimeSpan.FromMinutes(15)));

        var seek = WriteInvocation(client.PatchToSend(Report(TimeSpan.FromMinutes(15), seeked: true)));
        Assert.Contains("\"arguments\":[{\"Position\":\"00:15:00\",\"Seeked\":true}]", seek);

        Assert.Null(client.PatchToSend(Report(TimeSpan.FromMinutes(15))));
    }

    /// <summary>
    ///   <c>_lastState</c> is what <c>RegisterSession</c> and
    ///   <c>ReconnectSession</c> carry as the whole picture. A seek in it
    ///   would be replayed on every reconnect.
    /// </summary>
    [Fact]
    public async Task TheRegistrationBaseline_NeverHoldsASeek()
    {
        await using var client = new MediaSessionClient(
            "http://localhost:8111", "apikey", "device", new StubPlaybackCoordinator(),
            initialState: Report(TimeSpan.FromMinutes(1), seeked: true));
        Assert.Null(LastState(client)!.Seeked);

        await client.ReportStateAsync(Report(TimeSpan.FromMinutes(10), seeked: true));

        var baseline = LastState(client);
        Assert.NotNull(baseline);
        Assert.Null(baseline.Seeked);
        var frame = WriteInvocation(baseline);
        Assert.DoesNotContain("Seeked", frame);
        // The rest of the picture is all still there.
        Assert.Contains("\"Position\":\"00:10:00\"", frame);
        Assert.Contains("\"State\":\"playing\"", frame);
        Assert.Contains("\"Volume\":83", frame);
        Assert.Contains("\"CurrentItem\":{", frame);
    }

    /// <summary>
    ///   And the reclaim: the report after one is whole, and whole means
    ///   everything the session holds, which is no seek.
    /// </summary>
    [Fact]
    public async Task AReclaimAfterASeek_DoesNotReplayIt()
    {
        await using var client = new MediaSessionClient(
            "http://localhost:8111", "apikey", "device", new StubPlaybackCoordinator());
        var sessionId = Guid.NewGuid();
        client.SetSessionId(sessionId);
        client.PatchToSend(Report(TimeSpan.FromMinutes(1)));
        client.PatchToSend(Report(TimeSpan.FromMinutes(10), seeked: true));

        client.SetSessionId(sessionId);

        var frame = WriteInvocation(client.PatchToSend(Report(TimeSpan.FromMinutes(10))));
        Assert.Contains("\"State\":\"playing\"", frame);
        Assert.DoesNotContain("Seeked", frame);
    }

    [Fact]
    public void WithoutSeek_KeepsWhatTheReportNamed_AndOnlyThat()
    {
        var copy = new PlaybackStateUpdateDto { Position = TimeSpan.FromMinutes(3), Seeked = true }.WithoutSeek();

        Assert.Equal("{\"type\":1,\"target\":\"UpdateState\",\"arguments\":[{\"Position\":\"00:03:00\"}]}\u001e",
            WriteInvocation(copy));

        var plain = Report(TimeSpan.FromMinutes(3));
        Assert.Same(plain, plain.WithoutSeek());
    }
}
