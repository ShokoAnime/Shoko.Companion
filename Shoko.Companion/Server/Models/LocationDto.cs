namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing a physical file location on disk within a managed import folder.
/// </summary>
public class LocationDto
{
    /// <summary>
    /// The internal Shoko server ID for this location record.
    /// </summary>
    public int ID { get; set; }

    /// <summary>
    /// The ID of the file associated with this location.
    /// </summary>
    public int FileID { get; set; }

    /// <summary>
    /// The ID of the managed import folder that contains this file.
    /// </summary>
    public int ManagedFolderID { get; set; }

    /// <summary>
    /// The relative path of the file within the managed folder.
    /// </summary>
    public string? RelativePath { get; set; }

    /// <summary>
    /// Indicates whether the file at this location is currently accessible on disk.
    /// </summary>
    public bool IsAccessible { get; set; }
}
