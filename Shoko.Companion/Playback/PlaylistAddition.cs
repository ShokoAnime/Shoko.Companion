using System;

namespace Shoko.Companion.Playback;

/// <summary>
///   One item to add to the mpv playlist: the <c>shoko://</c> play URL to
///   resolve, and whatever the caller said about playing it once the queue
///   reaches it.
/// </summary>
/// <param name="ShokoUrl">
///   The <c>shoko://</c> play URL to resolve and add.
/// </param>
/// <param name="StartPosition">
///   Optional. Where this item starts when it plays. <c>null</c> says
///   nothing, and the stored progress for the file decides;
///   <see cref="TimeSpan.Zero"/> means the beginning.
/// </param>
public sealed record PlaylistAddition(string ShokoUrl, TimeSpan? StartPosition = null);
