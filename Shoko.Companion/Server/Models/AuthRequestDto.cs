using Newtonsoft.Json;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO for authenticating with the Shoko server via username/password.
/// </summary>
public class AuthRequestDto
{
    /// <summary>The Shoko username.</summary>
    [JsonProperty("user")]
    public string User { get; set; } = string.Empty;

    /// <summary>The Shoko password.</summary>
    [JsonProperty("pass")]
    public string Pass { get; set; } = string.Empty;

    /// <summary>A device name for the session.</summary>
    [JsonProperty("device")]
    public string Device { get; set; } = string.Empty;
}
