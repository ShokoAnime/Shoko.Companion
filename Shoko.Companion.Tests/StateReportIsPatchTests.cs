using System;
using System.Buffers;
using System.Text;
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
/// </summary>
public class StateReportIsPatchTests
{
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
}
