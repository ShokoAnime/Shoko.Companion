using System;

namespace Shoko.Companion.Playback;

/// <summary>
///   The single place this client spells a playback state for the media
///   session hub, and the single place it decides what a state means for
///   what a remote may do.
///
///   <para>
///     The hub's <c>State</c> field is a string on this side and an enum
///     on the server's, so nothing in either build type-checks the value:
///     a member renamed there is a client that still compiles here and
///     stops being understood. That is not hypothetical. The server's
///     <c>Loading</c> was split into <c>preparing</c> and
///     <c>buffering</c>, this client went on sending <c>"Loading"</c>,
///     and every load report failed argument binding at the hub — the
///     method never ran, so the server kept the state it already held and
///     nothing anywhere logged above Debug. Five copies of the same
///     switch statement across the app is what let one wrong name sit in
///     four of them.
///   </para>
///   <para>
///     <b>The names are the server's lowercase wire names, not this
///     enum's member names.</b> The contract declares them on the enum
///     itself and both JSON stacks are asked to honour them. Newtonsoft —
///     which is what the host's hub actually resolves for the
///     <c>json</c> protocol — matches them case-insensitively, so
///     <c>"Playing"</c> happened to work; System.Text.Json, the protocol
///     the plugin's own registration would supply, matches them exactly
///     and refuses every capitalised name including <c>"Playing"</c>.
///     Sending the lowercase form is understood by both, so this client
///     stops depending on which one wins a registration race it cannot
///     see.
///   </para>
/// </summary>
public static class PlaybackStateWire
{
    /// <summary>
    ///   The state name to report to the hub.
    /// </summary>
    /// <param name="state">The local playback state.</param>
    /// <returns>The server's wire name for <paramref name="state"/>.</returns>
    public static string ToWireName(this PlaybackState state) => state switch
    {
        PlaybackState.Idle => "idle",
        // Getting ready to play — resolving the source, building an index
        // — is what the server calls "preparing", and it is the member
        // that replaced the one this used to name.
        PlaybackState.Loading => "preparing",
        PlaybackState.Playing => "playing",
        PlaybackState.Paused => "paused",
        PlaybackState.Stopped => "stopped",
        PlaybackState.Error => "error",
        PlaybackState.Buffering => "buffering",
        // Unreachable while the switch stays total, and the wire-contract
        // test fails on any member that reaches it rather than letting a
        // new state quietly report itself as idle.
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No wire name for this playback state"),
    };

    /// <summary>
    ///   Read a state back from a name that crossed the wire. Used for
    ///   the state a reconnecting session starts from, which is the last
    ///   DTO this client built rather than anything the server said.
    /// </summary>
    /// <param name="wireName">A wire name, or <c>null</c>.</param>
    /// <returns>
    ///   The matching state, or <see cref="PlaybackState.Idle"/> for a
    ///   null or unrecognised name — a session whose state cannot be read
    ///   is not playing anything as far as this client may claim.
    /// </returns>
    public static PlaybackState FromWireName(string? wireName) => wireName?.ToLowerInvariant() switch
    {
        "preparing" => PlaybackState.Loading,
        "playing" => PlaybackState.Playing,
        "paused" => PlaybackState.Paused,
        "stopped" => PlaybackState.Stopped,
        "error" => PlaybackState.Error,
        "buffering" => PlaybackState.Buffering,
        _ => PlaybackState.Idle,
    };

    /// <summary>
    ///   Whether something is loaded and playable right now, which is
    ///   what the transport and frame capabilities are declared from.
    ///
    ///   <para>
    ///     <see cref="PlaybackState.Buffering"/> counts: the media is
    ///     fine and the position is real, only the pipe is behind, and a
    ///     session that goes deaf every time the cache runs dry is deaf
    ///     at exactly the moment somebody reaches for the remote.
    ///     <see cref="PlaybackState.Loading"/> does not: pre-processing a
    ///     file to build the frame index has produced nothing to seek in
    ///     or capture yet, so declaring those abilities there would be a
    ///     promise this client cannot keep.
    ///   </para>
    /// </summary>
    /// <param name="state">The local playback state.</param>
    /// <returns><c>true</c> when something playable is loaded.</returns>
    public static bool HasActivePlayback(this PlaybackState state)
        => state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering;

    /// <summary>
    ///   Whether a remote may stop this session, which is gated more
    ///   loosely than everything else on purpose.
    ///
    ///   <para>
    ///     Stopping is not control of playback, it is control of the
    ///     session's attention. A pre-process with no frame yet cannot be
    ///     sought or screenshotted — there is nothing in it to seek — but
    ///     it can certainly be abandoned, and a viewer who started the
    ///     wrong file should not have to wait out a frame-index build to
    ///     say so.
    ///   </para>
    /// </summary>
    /// <param name="state">The local playback state.</param>
    /// <returns><c>true</c> when there is something to abandon.</returns>
    public static bool CanAbandonPlayback(this PlaybackState state)
        => state.HasActivePlayback() || state is PlaybackState.Loading;
}
