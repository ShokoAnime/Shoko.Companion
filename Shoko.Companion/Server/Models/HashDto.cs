namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing a single cryptographic hash value for a media file.
/// </summary>
public class HashDto
{
    /// <summary>
    /// The type of hash (e.g., "MD5", "SHA1", "CRC32").
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// The hash value as a hexadecimal string.
    /// </summary>
    public string? Value { get; set; }
}
