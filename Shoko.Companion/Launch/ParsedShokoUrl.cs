using System.Diagnostics.CodeAnalysis;

namespace Shoko.Companion.Launch;

/// <summary>
/// Structured result of parsing a shoko:// URL.
/// Contains all extracted fields regardless of URL format.
/// </summary>
public class ParsedShokoUrl
{
    /// <summary>
    /// The detected action type.
    /// </summary>
    public required ShokoUrlAction Action { get; init; }

    /// <summary>
    /// Whether the URL is a play action.
    /// </summary>
    [MemberNotNullWhen(true, nameof(PlaylistId), nameof(ServerBaseUrl))]
    public bool IsPlayAction => Action == ShokoUrlAction.Play;

    /// <summary>
    /// Whether the URL is an open-folder action (relative, by managed folder ID).
    /// </summary>
    [MemberNotNullWhen(true, nameof(ManagedFolderId), nameof(ServerBaseUrl))]
    public bool IsOpenFolderAction => Action == ShokoUrlAction.OpenFolderRelative;

    /// <summary>
    /// Whether the URL is an open-folder-absolute action (by server-side absolute path).
    /// </summary>
    [MemberNotNullWhen(true, nameof(AbsolutePath), nameof(ServerBaseUrl))]
    public bool IsOpenFolderAbsoluteAction => Action == ShokoUrlAction.OpenFolderAbsolute;

    /// <summary>
    /// Server base URL including any sub-path (e.g. <c>http://myserver:8111</c>
    /// or <c>https://myserver:8111/virtual/client</c>).
    /// Present for all actions except invalid URLs.
    /// </summary>
    public string? ServerBaseUrl { get; init; }

    /// <summary>
    /// Playlist identifier (e.g. <c>s1234</c> or <c>e99</c>).
    /// Present for <see cref="ShokoUrlAction.Play"/>.
    /// </summary>
    public string? PlaylistId { get; init; }

    /// <summary>
    /// Managed folder ID from the open-folder URL.
    /// Present for <see cref="ShokoUrlAction.OpenFolderRelative"/>.
    /// </summary>
    public int? ManagedFolderId { get; init; }

    /// <summary>
    /// Optional relative path within the managed folder.
    /// Present for <see cref="ShokoUrlAction.OpenFolderRelative"/> when specified.
    /// </summary>
    public string? RelativePath { get; init; }

    /// <summary>
    /// Absolute server-side path (e.g. <c>/mnt/anime/Series/Show/ep.mkv</c>).
    /// Present for <see cref="ShokoUrlAction.OpenFolderAbsolute"/>.
    /// </summary>
    public string? AbsolutePath { get; init; }
}
