using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Protocol;
using Shoko.Companion.Playback;
using Shoko.Companion.Server;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   What this client spells into the media session hub's <c>State</c>
///   field, and whether the server can read it back.
///
///   <para>
///     This exists because of a failure nothing in either build could
///     see. The field is a string here and an enum there, so a member
///     renamed on the server is a client that still compiles, still
///     connects, still sends — and is silently not understood. That is
///     what happened: the server's <c>Loading</c> was split into
///     <c>preparing</c> and <c>buffering</c>, this client kept sending
///     <c>"Loading"</c>, and for thirteen days every load report failed
///     argument binding at the hub. The method never ran, the server kept
///     whatever state it already held, and the only trace was a Debug
///     line on this side. A transitional report that never lands looks
///     exactly like a state that passed quickly.
///   </para>
///   <para>
///     <b>What this can and cannot catch.</b> Nothing in this repository
///     references the server's enum, so no test here can notice a rename
///     the day it happens. What it can do is put the whole vocabulary in
///     one visible place and fail the moment a state is added here
///     without one — so the mapping is a thing somebody edits and reads,
///     rather than five copies of a switch statement of which one was
///     wrong. <see cref="MirroredMediaPlaybackState"/> below is a
///     deliberate copy of the contract, converters and all, and updating
///     it is the step that makes a contract change visible.
///   </para>
///   <para>
///     System.Text.Json appears here despite the app's "Newtonsoft for
///     everything" rule, and on purpose: the hub connection serialises
///     with it whatever this codebase prefers, and it is the stricter of
///     the two readers. A name that satisfies both is a name that does
///     not depend on which protocol a host happens to register.
///   </para>
/// </summary>
public class PlaybackStateWireTests
{
    /// <summary>
    ///   A copy of the server's <c>MediaPlaybackState</c>, with the two
    ///   converters the contract declares on the type. Kept here so a
    ///   name that crosses the wire is deserialised by the same machinery
    ///   that reads it on the far side rather than compared as a string.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    internal enum MirroredMediaPlaybackState
    {
        [EnumMember(Value = "idle")]
        [JsonStringEnumMemberName("idle")]
        Idle = 0,

        [EnumMember(Value = "preparing")]
        [JsonStringEnumMemberName("preparing")]
        Preparing = 1,

        [EnumMember(Value = "playing")]
        [JsonStringEnumMemberName("playing")]
        Playing = 2,

        [EnumMember(Value = "paused")]
        [JsonStringEnumMemberName("paused")]
        Paused = 3,

        [EnumMember(Value = "stopped")]
        [JsonStringEnumMemberName("stopped")]
        Stopped = 4,

        [EnumMember(Value = "error")]
        [JsonStringEnumMemberName("error")]
        Error = 5,

        [EnumMember(Value = "buffering")]
        [JsonStringEnumMemberName("buffering")]
        Buffering = 6,
    }

    /// <summary>
    ///   Every state this client can be in, and the server member it is
    ///   meant to land on. The table is the point of the file: one place
    ///   to read, one place to change.
    /// </summary>
    private static readonly Dictionary<PlaybackState, MirroredMediaPlaybackState> Expected = new()
    {
        [PlaybackState.Idle] = MirroredMediaPlaybackState.Idle,
        // The one that was wrong. Getting ready to play - resolving the
        // source, building the frame index - is what the server calls
        // preparing, and it is the member that replaced the one this used
        // to name.
        [PlaybackState.Loading] = MirroredMediaPlaybackState.Preparing,
        [PlaybackState.Playing] = MirroredMediaPlaybackState.Playing,
        [PlaybackState.Paused] = MirroredMediaPlaybackState.Paused,
        [PlaybackState.Stopped] = MirroredMediaPlaybackState.Stopped,
        [PlaybackState.Error] = MirroredMediaPlaybackState.Error,
        [PlaybackState.Buffering] = MirroredMediaPlaybackState.Buffering,
    };

    /// <summary>
    ///   The sweep. A state added without a wire name fails here rather
    ///   than at a hub that will not say so.
    /// </summary>
    [Fact]
    public void EveryState_HasAWireName()
    {
        var states = Enum.GetValues<PlaybackState>();

        Assert.Equal(states.Length, Expected.Count);
        foreach (var state in states)
        {
            Assert.True(Expected.ContainsKey(state), $"No wire name declared for {state}");
            Assert.False(string.IsNullOrWhiteSpace(state.ToWireName()));
        }
    }

    /// <summary>
    ///   The round trip, through both serialisers, because the answer has
    ///   to hold under either. The host's hub resolves Newtonsoft for the
    ///   <c>json</c> protocol; System.Text.Json is what the plugin's own
    ///   registration would supply, and it is the stricter of the two -
    ///   it matches the contract's names exactly and refuses every
    ///   capitalised form, <c>"Playing"</c> included. Sending the
    ///   lowercase wire names is what makes this client correct under
    ///   both rather than under whichever one wins.
    /// </summary>
    [Fact]
    public void EveryWireName_DeserialisesToTheIntendedMember()
    {
        foreach (var (state, expected) in Expected)
        {
            var json = $"\"{state.ToWireName()}\"";

            Assert.Equal(
                expected,
                System.Text.Json.JsonSerializer.Deserialize<MirroredMediaPlaybackState>(json));

            Assert.Equal(
                expected,
                Newtonsoft.Json.JsonConvert.DeserializeObject<MirroredMediaPlaybackState>(json));
        }
    }

    /// <summary>
    ///   The regression itself, kept as a test rather than a sentence.
    ///   <c>"Loading"</c> is not a member of the contract and never
    ///   became one; a report naming it does not arrive wrong, it does
    ///   not arrive at all.
    /// </summary>
    [Fact]
    public void TheRetiredName_IsRefusedByBothSerialisers()
    {
        Assert.DoesNotContain("Loading", Expected.Keys.Select(state => state.ToWireName()));

        Assert.ThrowsAny<Exception>(
            () => System.Text.Json.JsonSerializer.Deserialize<MirroredMediaPlaybackState>("\"Loading\""));
        Assert.ThrowsAny<Exception>(
            () => Newtonsoft.Json.JsonConvert.DeserializeObject<MirroredMediaPlaybackState>("\"Loading\""));
    }

    /// <summary>
    ///   And the name survives the trip out. The DTO carries Newtonsoft
    ///   attributes but the hub connection serialises with
    ///   System.Text.Json, so what is asserted here is the frame this
    ///   client actually puts on the wire rather than what the attributes
    ///   suggest it would.
    /// </summary>
    [Fact]
    public void TheEmittedFrame_CarriesTheWireName()
    {
        foreach (var state in Enum.GetValues<PlaybackState>())
        {
            var frame = WriteInvocation(new PlaybackStateUpdateDto { State = state.ToWireName() });

            Assert.Contains($"\"{state.ToWireName()}\"", frame);
        }
    }

    /// <summary>
    ///   Serialise a state report exactly as the hub connection does:
    ///   the default <see cref="JsonHubProtocol"/>, which is what
    ///   <c>HubConnectionBuilder.Build()</c> installs.
    /// </summary>
    private static string WriteInvocation(PlaybackStateUpdateDto state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        new JsonHubProtocol().WriteMessage(
            new InvocationMessage("UpdateState", [state]), buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
