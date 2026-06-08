namespace Shoko.Companion.Server.Models;

/// <summary>
/// A reference to an image hosted by the Shoko server.
/// </summary>
public class ImageDto
{
    /// <summary>Image source URL or identifier.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Image resource ID.</summary>
    public string ResourceID { get; set; } = string.Empty;

    /// <summary>Image type classification.</summary>
    public string Type { get; set; } = string.Empty;
}
