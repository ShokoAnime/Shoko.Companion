using System;
using System.Globalization;
using System.Linq;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Playback;

/// <summary>
///   What an item is called on screen: the series and episode titles, the
///   episode's place in its series, and the media title mpv shows in its
///   window and OSD.
///
///   <para>
///     Built from the media session server's answer while one is connected
///     (<see cref="FromSessionItem"/>); the companion's own reading of Shoko's
///     playlist is the fallback for when there is none.
///   </para>
/// </summary>
/// <param name="SeriesTitle">The series' title.</param>
/// <param name="EpisodeTitle">The episode's title.</param>
/// <param name="EpisodeNumber">The episode number, 0 when unknown.</param>
/// <param name="EpisodeCount">The series' episode count, 0 when unknown.</param>
/// <param name="MediaTitle">mpv's <c>force-media-title</c>.</param>
internal sealed record ItemTitles(
    string? SeriesTitle,
    string? EpisodeTitle,
    int EpisodeNumber,
    int EpisodeCount,
    string MediaTitle)
{
    /// <summary>
    ///   The titles the server gave an item, or <c>null</c> when it gave none
    ///   to show: neither a series nor an episode title, which is what an item
    ///   the server could not resolve looks like.
    /// </summary>
    public static ItemTitles? FromSessionItem(SessionItemInfoDto? item)
    {
        if (item is null)
            return null;

        var series = Blank(item.SeriesTitle);
        var episode = Blank(item.EpisodeTitle);
        if (series is null && episode is null)
            return null;

        return new ItemTitles(
            series,
            episode,
            item.EpisodeNumber ?? 0,
            item.EpisodeCount ?? 0,
            MediaTitleOf(series, EpisodeLabel(item.EpisodeType, item.EpisodeNumber), episode));
    }

    /// <summary>
    ///   The episode's number as Shoko's own playlist writes it: the number
    ///   for a regular episode, and the type's initial before it for anything
    ///   else (<c>S2</c> for the second special). Matching it keeps the window
    ///   title in one shape whichever source named the item.
    /// </summary>
    internal static string? EpisodeLabel(string? episodeType, int? episodeNumber)
    {
        if (episodeNumber is not { } number)
            return null;

        var digits = number.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(episodeType)
            || string.Equals(episodeType, "episode", StringComparison.OrdinalIgnoreCase))
            return digits;

        return char.ToUpperInvariant(episodeType[0]) + digits;
    }

    /// <summary>
    ///   <c>Series - Number - Episode</c>, the shape of Shoko's playlist
    ///   entries, leaving out whatever part is unknown.
    /// </summary>
    internal static string MediaTitleOf(string? series, string? label, string? episode)
        => string.Join(" - ", new[] { series, label, episode }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
