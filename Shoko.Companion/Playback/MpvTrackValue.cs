namespace Shoko.Companion.Playback;

/// <summary>
/// Translates one field of a media-session track selection into the value
/// mpv's <c>vid</c> / <c>aid</c> / <c>sid</c> property takes.
///
/// Its own type because it is the whole of the wire convention, and the
/// convention is load-bearing: the plugin's fields are three-valued and
/// each sentinel means something different from "a track number that
/// happens to be negative". Getting it wrong is silent - mpv would take
/// the wrong track, report it back cheerfully, and the ordinal would be
/// persisted and handed to the next client as a preference nobody set.
/// </summary>
public static class MpvTrackValue
{
    /// <summary>The verb that clears a choice back to the file's default.</summary>
    public const int Clear = -2;

    /// <summary>Subtitles deliberately off. Only valid on the subtitle index.</summary>
    public const int Off = -1;

    /// <summary>
    ///   The value to write to mpv, or <c>null</c> when nothing should be
    ///   written.
    ///
    ///   Null in means null out: a field that says nothing must leave that
    ///   kind of track alone, since a state report carries all three and
    ///   most of them are only talking about one.
    ///
    ///   An ordinal past the end of <paramref name="trackCount"/> also
    ///   writes nothing, and that is not an error. The ordinals describe
    ///   whatever file the sender was looking at, which may be a different
    ///   release of the same episode with fewer tracks; nobody asked for a
    ///   failure, they asked for a track this file turned out not to have,
    ///   and the file's own default is the honest answer.
    /// </summary>
    /// <param name="value">The selection field, as it arrived.</param>
    /// <param name="trackCount">
    ///   How many streams of this kind the current file has.
    /// </param>
    /// <param name="isSubtitle">
    ///   Whether this is the subtitle field, which alone accepts
    /// <see cref="Off"/>. A <c>-1</c> anywhere else is out-of-bounds
    ///   noise rather than a state, and is dropped.
    /// </param>
    public static object? Resolve(int? value, int trackCount, bool isSubtitle)
    {
        if (value is not { } ordinal)
            return null;

        // mpv's own word for "you pick", which is exactly what clearing a
        // stored choice asks for.
        if (ordinal == Clear)
            return "auto";

        if (isSubtitle && ordinal == Off)
            return "no";

        // mpv counts each kind from 1; the ordinal counts from 0.
        if (ordinal >= 0 && ordinal < trackCount)
            return ordinal + 1;

        return null;
    }

    /// <summary>
    ///   The ordinal an mpv per-kind id corresponds to, or <c>null</c> when
    ///   the track is disabled or the id names no stream this file has.
    /// </summary>
    /// <param name="mpvId">
    ///   mpv's 1-based id, or null when the property reads "no".
    /// </param>
    /// <param name="trackCount">
    ///   How many streams of this kind the current file has.
    /// </param>
    public static int? ToOrdinal(int? mpvId, int trackCount)
        => mpvId is { } id && id >= 1 && id <= trackCount ? id - 1 : null;
}
