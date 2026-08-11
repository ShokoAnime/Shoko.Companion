using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Shoko.Companion.Playback;

/// <summary>
///   Recognises the stream URLs the companion can meet, and rewrites MediaSession
///   ones so that the session they stream under is the companion's own.
///
///   <para>
///     Two shapes exist. Shoko's own <c>/api/v3/File/{id}/Stream</c>, which is
///     authenticated by API key and has no session concept; and the plugin's
///     <c>/api/plugin/MediaSession/v1/Stream/{videoId}[/...]</c>, which is anonymous
///     — a <c>&lt;video&gt;</c> element cannot send headers — and is instead
///     guarded on a <c>sessionId</c> query parameter naming a session whose
///     current, next or previous item is the requested video. Neither shape is
///     anchored to the start of the URL, because a Shoko behind a path base
///     serves both under that prefix.
///   </para>
///
///   <h3>The session-id policy, and why</h3>
///
///   <para>
///     <b>Every media session URL the companion plays carries the companion's own
///     session id — attached when the URL has none, replacing whatever is
///     there when it has one — and when the companion has no session id of
///     its own, the URL is swapped back for the plain APIv3 stream
///     endpoint.</b>
///   </para>
///
///   <para>
///     Replacing and attaching are one policy rather than two, because the
///     question they answer is one question: whose session is this stream
///     billed to. Kept apart, each is wrong on its own:
///   </para>
///
///   <list type="bullet">
///     <item>
///       <description>
///         <i>Replace only</i> has no defined behaviour for the case that
///         actually happens. The companion's session id arrives
///         asynchronously — <c>MediaSessionClient</c> holds none until
///         <c>RegisterSession</c> returns, and a playlist can be fetched
///         before the hub connects, or with the plugin absent entirely. A
///         replace-only rule degenerates to leaving the URL alone, which is
///         precisely playing under someone else's session id.
///       </description>
///     </item>
///     <item>
///       <description>
///         <i>Attach only</i> is a no-op in the one case that matters. A URL
///         that already carries a foreign <c>sessionId</c> is left carrying
///         it, so two players share session state and — once user-data writes
///         are keyed by session — one scrobbles under the other's name.
///       </description>
///     </item>
///     <item>
///       <description>
///         <i>Swap for APIv3</i> as the default surrenders remux, HLS and
///         transcode routing for every item whenever the plugin is present,
///         which is the whole reason the plugin exists. It is the right
///         <i>fallback</i> and the wrong default, so that is where it sits.
///       </description>
///     </item>
///   </list>
///
///   <para>
///     A foreign session id is therefore always rewritten, never honoured.
///     <see cref="ForPlayback"/> holds one invariant above all others: <b>the
///     URL it returns never carries a session id that is not ours</b>. When
///     neither attaching ours nor swapping back for APIv3 is available — a
///     media session sub-resource met while the companion holds no session id — it
///     strips the session id and leaves the endpoint's own guard to refuse the
///     URL with a 401. A loud failure on one item is the cheap mistake;
///     streaming and scrobbling under a stranger's session is the expensive
///     one.
///   </para>
/// </summary>
public static partial class StreamUrls
{
    /// <summary>
    /// The query parameter the plugin's stream endpoints are guarded on.
    /// </summary>
    private const string SessionIdParameter = "sessionId";

    /// <summary>
    /// The query parameter Shoko's APIv3 authenticates with.
    /// </summary>
    private const string ApiKeyParameter = "apikey";

    /// <summary>
    ///   Parse a URL as a stream URL of either family. Returns <c>null</c>
    ///   when it is neither — an image URL, a local file path, a playlist
    ///   URL, a comment line out of an m3u8.
    /// </summary>
    /// <param name="url">The URL, absolute or relative.</param>
    /// <returns>The parsed URL, or <c>null</c> when unrecognised.</returns>
    public static StreamUrlInfo? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (MediaSessionStreamRegex().Match(url) is { Success: true } mediaSession)
        {
            var resource = mediaSession.Groups[2].Success
                ? mediaSession.Groups[2].Value.TrimStart('/')
                : null;

            return new StreamUrlInfo
            {
                Url = url,
                Kind = StreamUrlKind.MediaSession,
                VideoId = int.Parse(mediaSession.Groups[1].Value),
                Resource = string.IsNullOrEmpty(resource) ? null : resource,
                SessionId = Guid.TryParse(GetQueryParameter(url, SessionIdParameter), out var session)
                    ? session
                    : null,
            };
        }

        if (ApiV3StreamRegex().Match(url) is { Success: true } apiV3)
        {
            return new StreamUrlInfo
            {
                Url = url,
                Kind = StreamUrlKind.ApiV3,
                VideoId = int.Parse(apiV3.Groups[1].Value),
            };
        }

        return null;
    }

    /// <summary>
    ///   Recover the video (Shoko file) ID a URL or path names, in either
    ///   family. This is what the three recognition sites want: an mpv
    ///   playlist entry's filename, mpv's <c>path</c> property, and the entry
    ///   lines of a fetched m3u8.
    /// </summary>
    /// <param name="url">The URL or path to read.</param>
    /// <returns>The video ID, or <c>null</c> when the URL names no video.</returns>
    public static int? TryGetVideoId(string? url)
        => Parse(url)?.VideoId;

    /// <summary>
    ///   Rewrite a URL so it is safe for this companion to play, per the
    ///   policy documented on <see cref="StreamUrls"/>.
    /// </summary>
    /// <param name="url">The URL to rewrite.</param>
    /// <param name="ownSessionId">
    ///   The companion's own media session session id, or <c>null</c> when it holds
    ///   none — the plugin is absent, the hub is not connected, or
    ///   registration has not returned yet.
    /// </param>
    /// <param name="apiKey">
    ///   An API key to put on an APIv3 fallback URL that carries none.
    ///   Ignored when the URL already has one.
    /// </param>
    /// <returns>
    ///   The URL to play, which never carries a session id that is not ours.
    ///   Unchanged when it is not a media session URL, or when it already carries our
    ///   own session id.
    /// </returns>
    public static string ForPlayback(string url, Guid? ownSessionId, string? apiKey = null)
    {
        if (Parse(url) is not { Kind: StreamUrlKind.MediaSession } info)
            return url;

        if (ownSessionId is { } own)
        {
            return info.SessionId == own
                ? url
                : SetQueryParameter(url, SessionIdParameter, own.ToString());
        }

        // No session id of our own. An entry point can be served by APIv3
        // instead; anything else is left to fail its own guard rather than
        // ride on the session id it arrived with.
        return (info.IsEntryPoint ? ToApiV3(info, apiKey) : null)
            ?? SetQueryParameter(url, SessionIdParameter, null);
    }

    /// <summary>
    ///   Swap a media session entry-point URL back for Shoko's plain APIv3 stream
    ///   endpoint on the same host and path base, giving up remux, HLS and
    ///   transcode routing for that item in exchange for a URL that needs no
    ///   session.
    /// </summary>
    /// <param name="info">The parsed media session URL.</param>
    /// <param name="apiKey">An API key to add when the URL carries none.</param>
    /// <returns>The APIv3 URL, or <c>null</c> when the URL is not the plugin's.</returns>
    private static string? ToApiV3(StreamUrlInfo info, string? apiKey)
    {
        if (MediaSessionStreamRegex().Match(info.Url) is not { Success: true } match)
            return null;

        // Everything before the plugin route is scheme, host and path base,
        // which APIv3 shares. The query rides along: the entry URLs Shoko
        // mints carry the metadata the companion reads back out of them
        // (epNo, animeId, posterUrl, …), and APIv3 ignores what it does not
        // know. Only sessionId is dropped, because an endpoint with no
        // session concept carrying a session id only misleads a log reader.
        var rebuilt = info.Url[..match.Index]
            + $"/api/v3/File/{info.VideoId}/Stream"
            + QueryAndFragmentOf(info.Url);

        rebuilt = SetQueryParameter(rebuilt, SessionIdParameter, null);

        if (!string.IsNullOrEmpty(apiKey) && GetQueryParameter(rebuilt, ApiKeyParameter) is null)
            rebuilt = SetQueryParameter(rebuilt, ApiKeyParameter, apiKey);

        return rebuilt;
    }

    /// <summary>
    /// Read a query parameter's raw (decoded) value, or <c>null</c> when absent.
    /// </summary>
    /// <param name="url">The URL to read.</param>
    /// <param name="name">The parameter name, matched case-insensitively.</param>
    /// <returns>The decoded value, or <c>null</c>.</returns>
    private static string? GetQueryParameter(string url, string name)
    {
        var query = QueryOf(url);
        if (query.Length == 0)
            return null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            var key = split < 0 ? pair : pair[..split];
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            return split < 0 ? string.Empty : Uri.UnescapeDataString(pair[(split + 1)..]);
        }

        return null;
    }

    /// <summary>
    ///   Set, replace or (with a <c>null</c> value) remove a query parameter,
    ///   preserving the order of every other parameter and any fragment.
    /// </summary>
    /// <param name="url">The URL to rewrite.</param>
    /// <param name="name">The parameter name, matched case-insensitively.</param>
    /// <param name="value">The new value, or <c>null</c> to remove it.</param>
    /// <returns>The rewritten URL.</returns>
    private static string SetQueryParameter(string url, string name, string? value)
    {
        var fragmentAt = url.IndexOf('#');
        var fragment = fragmentAt < 0 ? string.Empty : url[fragmentAt..];
        var withoutFragment = fragmentAt < 0 ? url : url[..fragmentAt];

        var queryAt = withoutFragment.IndexOf('?');
        var path = queryAt < 0 ? withoutFragment : withoutFragment[..queryAt];
        var query = queryAt < 0 ? string.Empty : withoutFragment[(queryAt + 1)..];

        var rebuilt = new StringBuilder();
        var replaced = false;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            var key = split < 0 ? pair : pair[..split];

            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                // Drop every existing occurrence; the first is replaced in
                // place so the parameter keeps its position.
                if (replaced || value is null)
                    continue;

                replaced = true;
                Append(rebuilt, name, value);
                continue;
            }

            if (rebuilt.Length > 0)
                rebuilt.Append('&');
            rebuilt.Append(pair);
        }

        if (!replaced && value is not null)
            Append(rebuilt, name, value);

        return rebuilt.Length == 0
            ? path + fragment
            : $"{path}?{rebuilt}{fragment}";

        static void Append(StringBuilder builder, string name, string value)
        {
            if (builder.Length > 0)
                builder.Append('&');
            builder.Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    /// <summary>
    /// The URL's query without its leading <c>?</c>, or an empty string.
    /// </summary>
    /// <param name="url">The URL to read.</param>
    /// <returns>The query, or an empty string.</returns>
    private static string QueryOf(string url)
    {
        var fragmentAt = url.IndexOf('#');
        var withoutFragment = fragmentAt < 0 ? url : url[..fragmentAt];
        var queryAt = withoutFragment.IndexOf('?');
        return queryAt < 0 ? string.Empty : withoutFragment[(queryAt + 1)..];
    }

    /// <summary>
    /// The URL's query and fragment, leading <c>?</c> and <c>#</c> included,
    /// or an empty string when it has neither.
    /// </summary>
    /// <param name="url">The URL to read.</param>
    /// <returns>The query and fragment, or an empty string.</returns>
    private static string QueryAndFragmentOf(string url)
    {
        var at = url.IndexOfAny(['?', '#']);
        return at < 0 ? string.Empty : url[at..];
    }

    /// <summary>
    ///   Shoko's APIv3 stream endpoint. Unanchored — a path base can sit in
    ///   front of it — and deliberately left as loose as the single regex it
    ///   replaces, so that no URL the companion recognised before stops being
    ///   recognised now.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"/File/(\d+)/Stream", RegexOptions.IgnoreCase)]
    private static partial Regex ApiV3StreamRegex();

    /// <summary>
    ///   the plugin's stream endpoints. Group 1 is the video ID, group 2 the
    ///   resource path under it — <c>/master.m3u8</c>,
    ///   <c>/Transcode/Video/12.m4s</c>, <c>/Info</c> and so on, absent for
    ///   the bare full-file stream. The lookahead keeps <c>/Stream/42abc</c>
    ///   from reading as video 42.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"/api/plugin/MediaSession/v1/Stream/(\d+)(/[^?#]*)?(?=[?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MediaSessionStreamRegex();
}
