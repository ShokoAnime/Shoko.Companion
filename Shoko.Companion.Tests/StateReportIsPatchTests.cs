using System;
using System.Buffers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   What a state report actually puts on the wire, serialised through
///   the protocol the hub connection is built with rather than through a
///   stand-in.
///
///   <para>
///     [#289]. A report is a patch: a field this client never named must
///     be <em>absent</em>, and a field it set to <c>null</c> must be
///     written as null, because the server tells the two apart and acts
///     on the difference. Before the connection took a Newtonsoft
///     protocol there was no way to say the first — every unset property
///     went out as an explicit null, so a deliberately minimal report
///     wrote null over a volume nobody had touched.
///   </para>
///   <para>
///     The protocol is built from
///     <see cref="MediaSessionClient.ConfigureHubPayload"/>, the same
///     method <c>ConnectAsync</c> hands to
///     <c>AddNewtonsoftJsonProtocol</c>. Rebuilding the settings here
///     would let the two drift, and the drift would be invisible: the
///     frames would still be valid JSON and the server would still bind
///     most of them.
///   </para>
///   <para>
///     [#290] added what is sent rather than what can be. A report says
///     what moved since the last one the server accepted, and two rules
///     hold it together: the first report of a session is whole, because
///     SignalR only orders messages within a connection and a patch
///     against a baseline that is gone describes nothing; and a report
///     that moves nothing is not sent at all, so this client goes quiet
///     when the playback does.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class StateReportIsPatchTests
{
    private static MediaSessionClient NewClient()
        => new("http://localhost:8111", "apikey", "device", new StubPlaybackCoordinator());

    /// <summary>
    ///   Everything a call site fills in, the way all five of them do.
    /// </summary>
    private static PlaybackStateUpdateDto WholeReport(
        TimeSpan position, string state = "playing", int volume = 83)
        => new()
        {
            State = state,
            CurrentItem = new MediaItemInfoDto { Title = "Episode 1", VideoId = 7 },
            NextItem = new MediaItemInfoDto { Title = "Episode 2", VideoId = 8 },
            PreviousItem = null,
            Position = position,
            Duration = TimeSpan.FromMinutes(24),
            IsPaused = false,
            Volume = volume,
            IsMuted = false,
            PlaybackSpeed = 1.0,
            IsFullscreen = true,
            Tracks = new PlaybackTrackSelectionDto
            {
                VideoOrdinal = 0,
                AudioOrdinal = 1,
                SubtitleIndex = -1,
            },
        };

    private static string WriteInvocation(object payload)
    {
        var options = new NewtonsoftJsonHubProtocolOptions();
        MediaSessionClient.ConfigureHubPayload(options);

        var buffer = new ArrayBufferWriter<byte>();
        new NewtonsoftJsonHubProtocol(Options.Create(options))
            .WriteMessage(new InvocationMessage("UpdateState", [payload]), buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///   The distinction itself, on one frame: some fields set, one set
    ///   explicitly to null, the rest never touched.
    /// </summary>
    [Fact]
    public void AnUnsetFieldIsAbsentAndAnExplicitNullIsNull()
    {
        var frame = WriteInvocation(new PlaybackStateUpdateDto
        {
            State = "playing",
            Position = TimeSpan.FromMinutes(1),
            Volume = 83,
            Duration = null,
        });

        Assert.Contains("\"State\":\"playing\"", frame);
        Assert.Contains("\"Position\":\"00:01:00\"", frame);
        Assert.Contains("\"Volume\":83", frame);
        Assert.Contains("\"Duration\":null", frame);

        Assert.DoesNotContain("CurrentItem", frame);
        Assert.DoesNotContain("NextItem", frame);
        Assert.DoesNotContain("PreviousItem", frame);
        Assert.DoesNotContain("IsPaused", frame);
        Assert.DoesNotContain("IsMuted", frame);
        Assert.DoesNotContain("PlaybackSpeed", frame);
        Assert.DoesNotContain("IsFullscreen", frame);
        Assert.DoesNotContain("Tracks", frame);
    }

    /// <summary>
    ///   A report that names nothing carries nothing. The empty payload
    ///   is the shape the mechanism has to get right — every field of it
    ///   used to arrive as an instruction to erase.
    /// </summary>
    [Fact]
    public void AReportThatNamesNothingSendsAnEmptyObject()
    {
        Assert.Contains("\"arguments\":[{}]", WriteInvocation(new PlaybackStateUpdateDto()));
    }

    /// <summary>
    ///   Every field, so a property added without the flag pair fails
    ///   here rather than silently reverting to the old behaviour.
    /// </summary>
    [Fact]
    public void EveryFieldCanBeOmittedAndEveryFieldCanBeNamed()
    {
        var all = WriteInvocation(new PlaybackStateUpdateDto
        {
            State = "idle",
            CurrentItem = null,
            NextItem = null,
            PreviousItem = null,
            Position = TimeSpan.Zero,
            Duration = null,
            IsPaused = false,
            Volume = null,
            IsMuted = null,
            PlaybackSpeed = null,
            IsFullscreen = null,
            Tracks = null,
        });

        var nothing = WriteInvocation(new PlaybackStateUpdateDto());

        foreach (var name in new[]
        {
            "State", "CurrentItem", "NextItem", "PreviousItem", "Position", "Duration",
            "IsPaused", "Volume", "IsMuted", "PlaybackSpeed", "IsFullscreen", "Tracks",
        })
        {
            Assert.Contains($"\"{name}\":", all);
            Assert.DoesNotContain($"\"{name}\":", nothing);
        }
    }

    /// <summary>
    ///   The names the DTOs declare are the names on the wire. SignalR's
    ///   own Newtonsoft default camel-cases them and overrides specified
    ///   names while doing it, so this pins the resolver rather than the
    ///   protocol.
    /// </summary>
    [Fact]
    public void TheDeclaredPropertyNamesAreWhatTravels()
    {
        var frame = WriteInvocation(new PlaybackStateUpdateDto
        {
            State = "playing",
            CurrentItem = new MediaItemInfoDto { Title = "Episode 1", VideoId = 7 },
            Tracks = new PlaybackTrackSelectionDto { AudioOrdinal = 1 },
        });

        Assert.Contains("\"CurrentItem\":{", frame);
        Assert.Contains("\"Title\":\"Episode 1\"", frame);
        Assert.Contains("\"VideoId\":7", frame);
        Assert.Contains("\"AudioOrdinal\":1", frame);
        Assert.DoesNotContain("\"currentItem\"", frame);
        Assert.DoesNotContain("\"state\"", frame);
    }

    /// <summary>
    ///   Rule one. A session that has just registered or just reclaimed
    ///   its id is told everything, because what the server holds for it
    ///   is either nothing or something from a connection this client can
    ///   no longer describe deltas against.
    /// </summary>
    [Fact]
    public async Task TheFirstReportOfASessionIsWhole()
    {
        await using var client = NewClient();
        client.SetSessionId(Guid.NewGuid());

        var report = WholeReport(TimeSpan.FromMinutes(1));
        var frame = WriteInvocation(Assert.IsType<PlaybackStateUpdateDto>(client.PatchToSend(report)));

        foreach (var name in new[]
        {
            "State", "CurrentItem", "NextItem", "PreviousItem", "Position", "Duration",
            "IsPaused", "Volume", "IsMuted", "PlaybackSpeed", "IsFullscreen", "Tracks",
        })
            Assert.Contains($"\"{name}\":", frame);
    }

    /// <summary>
    ///   Rule one again, and the one a refactor will break quietly: the
    ///   client patched its way to a correct picture, the socket dropped,
    ///   and the session was reclaimed. Whatever the server holds now, it
    ///   is not the baseline those patches were written against.
    /// </summary>
    [Fact]
    public async Task AReclaimedSessionIsToldEverythingAgain()
    {
        await using var client = NewClient();
        var sessionId = Guid.NewGuid();
        client.SetSessionId(sessionId);

        client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1)));
        Assert.Null(client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1))));

        // What ReconnectSession does with the id it reclaimed.
        client.SetSessionId(sessionId);

        var frame = WriteInvocation(Assert.IsType<PlaybackStateUpdateDto>(
            client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1)))));
        Assert.Contains("\"State\":\"playing\"", frame);
        Assert.Contains("\"Volume\":83", frame);
        Assert.Contains("\"CurrentItem\":{", frame);
    }

    /// <summary>
    ///   The point of the whole thing, on the wire: a position tick during
    ///   playback moves the position and nothing else, so that is the
    ///   entire frame.
    /// </summary>
    [Fact]
    public async Task APositionTickSendsThePositionAndNothingElse()
    {
        await using var client = NewClient();
        client.SetSessionId(Guid.NewGuid());

        client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1)));
        var frame = WriteInvocation(Assert.IsType<PlaybackStateUpdateDto>(
            client.PatchToSend(WholeReport(TimeSpan.FromMinutes(2)))));

        Assert.Contains("\"arguments\":[{\"Position\":\"00:02:00\"}]", frame);
    }

    /// <summary>
    ///   Rule two. Nothing moved is nothing to say — and the items and the
    ///   track selection are rebuilt on every read, so this also pins the
    ///   value equality that keeps them off the wire.
    /// </summary>
    [Fact]
    public async Task AReportThatMovesNothingIsNotSent()
    {
        await using var client = NewClient();
        client.SetSessionId(Guid.NewGuid());

        client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1)));

        Assert.Null(client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1))));
    }

    /// <summary>
    ///   A field the server was never told is sent whatever it holds
    ///   locally: silence about a field is not a claim about its value.
    ///   The stopped-to-idle follow-up is the report that names a handful
    ///   of fields on purpose, so it is the baseline used here.
    /// </summary>
    [Fact]
    public async Task AFieldTheBaselineNeverNamedIsSent()
    {
        await using var client = NewClient();
        client.SetSessionId(Guid.NewGuid());

        client.PatchToSend(new PlaybackStateUpdateDto
        {
            State = "stopped",
            Position = TimeSpan.Zero,
            Volume = 83,
        });

        var frame = WriteInvocation(Assert.IsType<PlaybackStateUpdateDto>(
            client.PatchToSend(WholeReport(TimeSpan.Zero, "playing"))));

        Assert.Contains("\"CurrentItem\":{", frame);
        Assert.Contains("\"Tracks\":{", frame);
        Assert.DoesNotContain("\"Volume\":", frame);
        Assert.DoesNotContain("\"Position\":", frame);
    }

    /// <summary>
    ///   A reported idle clears the item triple, the position, the
    ///   duration and the tracks on the far side, so the report that says
    ///   it cannot be a baseline for the ones after it — the same item
    ///   playing again would otherwise be omitted as unchanged, against a
    ///   server holding none.
    /// </summary>
    [Fact]
    public async Task WhatFollowsAnIdleReportIsWhole()
    {
        await using var client = NewClient();
        client.SetSessionId(Guid.NewGuid());

        client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1)));
        client.PatchToSend(new PlaybackStateUpdateDto
        {
            State = "idle",
            Position = TimeSpan.Zero,
            Duration = null,
            IsPaused = false,
            Volume = 83,
            IsMuted = false,
            PlaybackSpeed = 1.0,
            IsFullscreen = true,
        });

        var frame = WriteInvocation(Assert.IsType<PlaybackStateUpdateDto>(
            client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1)))));

        Assert.Contains("\"CurrentItem\":{", frame);
        Assert.Contains("\"Volume\":83", frame);
    }

    /// <summary>
    ///   A report that names no state is not a report of idle, however the
    ///   property reads when nobody set it — so it does not throw the
    ///   baseline away, and the volume it did name stays known.
    /// </summary>
    [Fact]
    public async Task AReportThatNamesNoStateIsNotAnIdleReport()
    {
        await using var client = NewClient();
        client.SetSessionId(Guid.NewGuid());

        client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1)));
        client.PatchToSend(new PlaybackStateUpdateDto { Volume = 40 });

        var frame = WriteInvocation(Assert.IsType<PlaybackStateUpdateDto>(
            client.PatchToSend(WholeReport(TimeSpan.FromMinutes(1), volume: 40))));

        Assert.DoesNotContain("\"Volume\":", frame);
    }
}
