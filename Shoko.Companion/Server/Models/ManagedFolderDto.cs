namespace Shoko.Companion.Server.Models;

/// <summary>
/// Represents a managed (import) folder on the Shoko server.
/// Returned from the <c>/api/v3/ManagedFolder</c> endpoint.
/// </summary>
public class ManagedFolderDto
{
    /// <summary>
    /// Server-side ID for this managed folder.
    /// Referenced in open-folder URLs via <c>managedFolder</c> parameter.
    /// </summary>
    public int ID { get; set; }

    /// <summary>
    /// Absolute path on the server (e.g. <c>/mnt/anime/Series</c>).
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Display name for the folder.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Whether the import folder is currently enabled on the server.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}
