using System;
using System.Collections.Generic;
using System.IO;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Playback;

/// <summary>
///   Which subtitle files beside a video the companion hands mpv, and in
///   what order.
///
///   <para>
///     mpv does not look for subtitles beside an HTTP URL the way it does
///     beside a local file, so without <c>sub-add</c> they never reach it.
///     Order matters as much as presence: the companion maps a subtitle's
///     position in Shoko's list to mpv's <c>sid</c> (position + 1), and
///     Shoko lists the external files after every embedded stream. Adding
///     them in that order is what gives those positions a real mpv track.
///   </para>
/// </summary>
internal static class ExternalSubtitles
{
    /// <summary>
    ///   The external subtitles to add, in the order to add them: the run of
    ///   external entries at the end of the list, one per file, stopping at
    ///   the first one mpv cannot be given. Every file up to that point keeps
    ///   the position the rest of the companion assumes; one skipped in the
    ///   middle would shift every file after it.
    /// </summary>
    /// <param name="mediaInfo">The file's media info from Shoko.</param>
    /// <returns>The subtitles to add, possibly none.</returns>
    public static IReadOnlyList<MediaStreamDto> ToAdd(MediaInfoDto mediaInfo)
    {
        var subtitles = mediaInfo.Subtitles;
        var firstExternal = subtitles.FindIndex(s => s.IsExternal);
        if (firstExternal < 0)
            return [];

        // An embedded stream after an external one breaks the position
        // mapping whatever is added, so add nothing rather than something
        // that selects the wrong track.
        if (subtitles.FindIndex(firstExternal, s => !s.IsExternal) >= 0)
            return [];

        var result = new List<MediaStreamDto>();
        for (var i = firstExternal; i < subtitles.Count; i++)
        {
            if (subtitles[i] is not { ExternalFilename: { Length: > 0 } filename } subtitle || !CanAdd(filename))
                break;

            // One file can list more than one stream (VobSub does); mpv reads
            // them all from a single add, and a second add would double them.
            if (result.Count > 0 && result[^1].ExternalFilename == filename)
                break;

            result.Add(subtitle);
        }

        return result;
    }

    /// <summary>
    ///   Whether mpv can load the file from its URL alone. VobSub cannot: its
    ///   <c>.idx</c> names a <c>.sub</c> that mpv looks for beside the URL,
    ///   and Shoko serves only the files its media info lists.
    /// </summary>
    /// <param name="filename">The external file's name.</param>
    /// <returns><c>true</c> when mpv can load it.</returns>
    private static bool CanAdd(string filename)
        => !Path.GetExtension(filename).Equals(".idx", StringComparison.OrdinalIgnoreCase);
}
