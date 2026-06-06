namespace Shoko.Companion.Launch;

/// <summary>
/// The type of action requested by a parsed shoko:// URL.
/// </summary>
public enum ShokoUrlAction
{
    /// <summary>
    /// Unknown action.
    /// </summary>
    Unknown,

    /// <summary>
    /// Play action: <c>shoko:[//][http[s]://]host[/&lt;path&gt;]/play?playlist=&lt;id&gt;</c>
    /// </summary>
    Play,

    /// <summary>
    /// Open-folder action: <c>shoko:[protocol://]host/open-folder?managedFolder=&lt;id&gt;[&amp;relativePath=...]</c>
    /// </summary>
    OpenFolderRelative,

    /// <summary>
    /// Open-folder-absolute action: <c>shoko:[protocol://]host/open-folder?path=/absolute/path</c>
    /// </summary>
    OpenFolderAbsolute,
}
