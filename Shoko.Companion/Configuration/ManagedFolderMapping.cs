namespace Shoko.Companion.Configuration;

/// <summary>
/// Maps a managed folder (identified by <see cref="Id"/>) to a local path.
/// Used by the open-folder action: <c>open-folder?managedFolder=&lt;id&gt;&amp;relativePath=...</c>
/// </summary>
public class ManagedFolderMapping
{
    /// <summary>
    /// Unique stable identifier for this mapping. Referenced from open-folder URLs
    /// via the <c>managedFolder</c> query parameter.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Display name of the managed folder on the Shoko server.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path on the Shoko server (e.g. <c>/mnt/anime/Series</c>).
    /// Used to determine which server folder this mapping covers.
    /// </summary>
    public string ServerPath { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path on the local machine (e.g. <c>/home/user/media/anime</c>
    /// or <c>D:\Anime\Series</c>).
    /// </summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>
    /// Returns a string representation of the mapping.
    /// </summary>
    public override string ToString() => $"#{Id}: {Name} ({ServerPath}) → {LocalPath}";
}
