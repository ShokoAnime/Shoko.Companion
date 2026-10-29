using System;
using Newtonsoft.Json;

namespace Shoko.Companion.Server.Models;

/// <summary>
///   This companion's own session as the media session plugin describes it:
///   the plugin's <c>MediaSessionInfo</c>, as returned by
///   <c>RegisterSession</c> and <c>ReconnectSession</c> and pushed through
///   <c>SessionStateChanged</c> whenever the session changes.
///
///   <para>
///     The server sends the session's own connection the unmasked copy, so
///     the items here carry their titles even when they are private. Only
///     the fields this client reads are bound; Newtonsoft drops the rest.
///   </para>
/// </summary>
public sealed class MediaSessionInfoDto
{
    /// <summary>
    ///   The session this describes. Checked before anything is taken from
    ///   it: a description of somebody else's session is not ours to show.
    /// </summary>
    [JsonProperty("SessionId")]
    public Guid SessionId { get; init; }

    /// <summary>
    ///   The item before the current one, as the server resolved it.
    /// </summary>
    [JsonProperty("PreviousItem")]
    public SessionItemInfoDto? PreviousItem { get; init; }

    /// <summary>
    ///   The item playing, as the server resolved it.
    /// </summary>
    [JsonProperty("CurrentItem")]
    public SessionItemInfoDto? CurrentItem { get; init; }

    /// <summary>
    ///   The item after the current one, as the server resolved it.
    /// </summary>
    [JsonProperty("NextItem")]
    public SessionItemInfoDto? NextItem { get; init; }
}
