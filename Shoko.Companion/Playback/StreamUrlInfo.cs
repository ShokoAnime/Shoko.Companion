using System;

namespace Shoko.Companion.Playback;

/// <summary>
/// A stream URL the companion recognised, broken into the parts it needs:
/// which family it belongs to, which video it names, and — for media session URLs
/// — which session it was minted for and which resource under the video it
/// addresses.
/// </summary>
public sealed record StreamUrlInfo
{
    /// <summary>
    /// The URL exactly as it was parsed, query and fragment included.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Which endpoint family the URL belongs to.
    /// </summary>
    public required StreamUrlKind Kind { get; init; }

    /// <summary>
    /// The video (Shoko file) ID the URL names. the plugin's <c>videoId</c> and
    /// APIv3's <c>{id}</c> are the same identifier.
    /// </summary>
    public required int VideoId { get; init; }

    /// <summary>
    /// Everything the URL's path carries after <c>/Stream/{videoId}/</c>,
    /// with no leading slash — <c>master.m3u8</c>, <c>media.m3u8</c>,
    /// <c>Info</c>, <c>Transcode/Video/12.m4s</c> and so on. <c>null</c> for
    /// the bare full-file stream and for every APIv3 URL.
    /// </summary>
    public string? Resource { get; init; }

    /// <summary>
    /// The <c>sessionId</c> query parameter the URL carries, or <c>null</c>
    /// when it carries none or carries one that is not a GUID. Always
    /// <c>null</c> for APIv3 URLs, which have no session concept.
    /// </summary>
    public Guid? SessionId { get; init; }

    /// <summary>
    ///   Whether this URL is something a player can be handed as an entry
    ///   point, and therefore something the APIv3 fallback can stand in for.
    ///   True for APIv3 URLs, for the plugin's bare <c>/Stream/{videoId}</c>, and
    ///   for a master playlist.
    ///
    ///   A media playlist is deliberately excluded: the plugin serves a master at
    ///   every entry point and never hands out a <c>media.m3u8</c> as one, so
    ///   meeting a media playlist where an entry was expected is a surprise
    ///   worth failing loudly on rather than silently substituting a whole
    ///   file for. Segment, init, <c>Info</c>, subtitle and attachment URLs
    ///   are excluded for the plainer reason that a full-file stream is not
    ///   what they asked for.
    /// </summary>
    public bool IsEntryPoint => Kind is StreamUrlKind.ApiV3
        || Resource is null
        || (Resource.StartsWith("master", StringComparison.OrdinalIgnoreCase)
            && Resource.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
}
