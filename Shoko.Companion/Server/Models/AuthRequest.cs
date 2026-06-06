using Newtonsoft.Json;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// Request payload for authenticating with the Shoko server.
/// </summary>
public class AuthRequest
{
    /// <summary>
    /// The username for authentication.
    /// </summary>
    [JsonProperty("user")]
    public string User { get; set; } = string.Empty;

    /// <summary>
    /// The password for authentication.
    /// </summary>
    [JsonProperty("pass")]
    public string Pass { get; set; } = string.Empty;

    /// <summary>
    /// The device identifier associated with the authentication request.
    /// </summary>
    [JsonProperty("device")]
    public string Device { get; set; } = string.Empty;
}
