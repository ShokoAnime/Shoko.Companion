using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   A stopped session reports <c>idle</c> a few seconds after it stops.
///
///   <para>
///     The stopped state only exists so that watchers see the stop before
///     they see nothing. It used to last 10 s, which kept a device that had
///     stopped in every handoff list for that long; revam, 2026-10-21, put
///     it at 2 to 5 s (Kaname #340), and it is 3.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class StoppedToIdleTests
{
    [Fact]
    public void TheDelay_IsThreeSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(3), MediaSessionClient.StoppedToIdleDelay);

    [Fact]
    public async Task AStoppedReport_IsFollowedByIdle_AfterTheDelay()
    {
        var idleAt = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentQueue<string?>();
        var clock = Stopwatch.StartNew();

        await using var client = new MediaSessionClient(
            "http://localhost:8111", "apikey", "device", new StubPlaybackCoordinator());
        client.SetSessionId(Guid.NewGuid());
        client.HubSendOverride = (method, args) =>
        {
            if (method == "UpdateState" && args.FirstOrDefault() is PlaybackStateUpdateDto s)
            {
                states.Enqueue(s.State);
                if (s.State == "idle")
                    idleAt.TrySetResult(clock.Elapsed);
            }
            return Task.CompletedTask;
        };

        clock.Restart();
        await client.ReportStateAsync(new PlaybackStateUpdateDto
        {
            State = "stopped",
            CurrentItem = new MediaItemInfoDto { Title = "Episode 1", VideoId = 7 },
            Position = TimeSpan.FromMinutes(1),
            IsPaused = false,
            Volume = 83,
        });

        var finished = await Task.WhenAny(idleAt.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.True(finished == idleAt.Task, "no idle report within 8 s of the stop");

        var elapsed = await idleAt.Task;
        Assert.InRange(elapsed, TimeSpan.FromSeconds(2.9), TimeSpan.FromSeconds(6));
        Assert.Equal(["stopped", "idle"], states);
    }
}
