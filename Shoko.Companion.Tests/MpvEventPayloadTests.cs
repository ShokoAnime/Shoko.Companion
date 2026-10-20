using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Shoko.Companion.Mpv;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   What <see cref="MpvIpcClient"/> hands on for an mpv event.
///
///   <para>
///     mpv writes an event's own fields at the top level of the message,
///     beside <c>event</c>: <c>end-file</c> carries <c>reason</c>,
///     <c>playlist_entry_id</c> and, for an error, <c>file_error</c> there.
///     The client handed on <c>data ?? args</c>, which is <c>null</c> for
///     every such event, so no <c>end-file</c> reason ever reached the
///     coordinator: not an <c>eof</c>, and not an <c>error</c>.
///   </para>
/// </summary>
public class MpvEventPayloadTests
{
    private static List<MpvEventArgs> Events(string line)
    {
        var client = new MpvIpcClient();
        var events = new List<MpvEventArgs>();
        client.MpvEvent += (_, e) => events.Add(e);
        client.ProcessIncomingMessage(line);
        return events;
    }

    [Fact]
    public void EndFile_HandsOnItsFields()
    {
        var e = Assert.Single(Events(
            """{"event":"end-file","reason":"error","playlist_entry_id":2,"file_error":"loading failed"}"""));

        var data = Assert.IsType<JObject>(e.Data);
        Assert.Equal("end-file", e.Event);
        Assert.Equal("error", data["reason"]?.ToString());
        Assert.Equal(2L, data["playlist_entry_id"]?.ToObject<long>());
        Assert.Equal("loading failed", data["file_error"]?.ToString());
    }

    [Fact]
    public void StartFile_HandsOnItsEntryId()
    {
        var e = Assert.Single(Events("""{"event":"start-file","playlist_entry_id":3}"""));
        Assert.Equal(3L, Assert.IsType<JObject>(e.Data)["playlist_entry_id"]?.ToObject<long>());
    }

    [Fact]
    public void PropertyChange_HandsOnItsValue()
    {
        var e = Assert.Single(Events("""{"event":"property-change","id":1,"name":"pause","data":true}"""));
        Assert.Equal(true, (e.Data as JValue)?.Value);
    }

    [Fact]
    public void PropertyChange_WithNoValue_HandsOnNothing()
    {
        // An unavailable property: the message in its place would be read
        // as the property's value.
        var e = Assert.Single(Events("""{"event":"property-change","id":1,"name":"time-pos"}"""));
        Assert.Null(e.Data);
    }

    [Fact]
    public void ClientMessage_HandsOnItsArgs()
    {
        var e = Assert.Single(Events("""{"event":"client-message","args":["shoko-companion-toggle-privacy"]}"""));
        Assert.Equal("shoko-companion-toggle-privacy", Assert.IsType<JArray>(e.Data)[0]?.ToString());
    }
}
