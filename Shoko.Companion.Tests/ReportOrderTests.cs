using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shoko.Companion.Playback;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   Reports reach the hub in the order they were made.
///
///   <para>
///     Since Kaname #335 the server believes what a session reports and
///     applies no command itself, so the last report to arrive is the state
///     every observer sees. Each report used to be its own fire-and-forget
///     task that awaited a capability push before it sent, so a report held
///     up there was overtaken by the next one and landed after it: the
///     server ended on the older state, and the patch baseline on this side
///     ended on the older one too.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class ReportOrderTests
{
    private static PlaybackStateUpdateDto Report(string state, TimeSpan position)
        => new()
        {
            State = state,
            CurrentItem = new MediaItemInfoDto { Title = "Episode 1", VideoId = 7 },
            Position = position,
            Duration = TimeSpan.FromMinutes(24),
            IsPaused = state == PlaybackState.Paused.ToWireName(),
            Volume = 83,
        };

    [Fact]
    public async Task ASlowCapabilityPush_DoesNotLetALaterReportOvertakeAnEarlierOne()
    {
        var sent = new ConcurrentQueue<(string Method, object? Payload)>();
        var slowCapabilities = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capabilityPushes = 0;

        await using var client = new MediaSessionClient(
            "http://localhost:8111", "apikey", "device", new StubPlaybackCoordinator());
        client.SetSessionId(Guid.NewGuid());
        client.HubSendOverride = (method, args) =>
        {
            sent.Enqueue((method, args.FirstOrDefault()));
            // The first declaration of the session is the slow one.
            return method == "UpdateCapabilities" && ++capabilityPushes == 1
                ? slowCapabilities.Task
                : Task.CompletedTask;
        };

        var playing = client.ReportStateAsync(Report("playing", TimeSpan.FromMinutes(1)));
        var paused = client.ReportStateAsync(Report("paused", TimeSpan.FromMinutes(2)));

        // While the first report waits on its declaration, the second does
        // not go out ahead of it.
        await Task.Delay(200);
        Assert.DoesNotContain(sent, s => s.Method == "UpdateState");

        slowCapabilities.SetResult();
        await Task.WhenAll(playing, paused);

        var states = sent.Where(s => s.Method == "UpdateState")
            .Select(s => (PlaybackStateUpdateDto)s.Payload!)
            .ToList();
        Assert.Equal(2, states.Count);
        Assert.Equal("playing", states[0].State);
        Assert.Equal(TimeSpan.FromMinutes(1), states[0].Position);
        // The second is a patch against the first, which is the report the
        // server actually holds before it.
        Assert.Equal("paused", states[1].State);
        Assert.Equal(TimeSpan.FromMinutes(2), states[1].Position);
        Assert.False(states[1].ShouldSerializeCurrentItem());

        // And the baseline is the newer report: saying it again says nothing.
        await client.ReportStateAsync(Report("paused", TimeSpan.FromMinutes(2)));
        Assert.Equal(2, sent.Count(s => s.Method == "UpdateState"));
    }

    [Fact]
    public async Task EverySend_GoesOutInCallOrder()
    {
        var sent = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = new MediaSessionClient(
            "http://localhost:8111", "apikey", "device", new StubPlaybackCoordinator());
        client.SetSessionId(Guid.NewGuid());
        client.HubSendOverride = (method, args) =>
        {
            var payload = args.FirstOrDefault();
            sent.Enqueue(payload is PlaybackStateUpdateDto s ? $"{method}:{s.State}" : method);
            // Everything is slow until the test lets it through.
            return gate.Task;
        };

        var calls = new List<Task>
        {
            client.ReportStateAsync(Report("playing", TimeSpan.FromMinutes(1))),
            client.ReportPlaylistAsync([new MediaItemInfoDto { Title = "Episode 1", VideoId = 7 }]),
            client.ReportStateAsync(Report("paused", TimeSpan.FromMinutes(1))),
            client.ReportStateAsync(Report("stopped", TimeSpan.FromMinutes(1))),
        };

        await Task.Delay(100);
        // Nothing past the first send is on the wire while it is in flight.
        Assert.Equal(["UpdateCapabilities"], sent);

        gate.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(
            ["UpdateCapabilities", "UpdateState:playing", "UpdatePlaylist", "UpdateState:paused", "UpdateState:stopped"],
            sent);
    }
}
