using Newtonsoft.Json;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// Response payload returned after a successful authentication request.
/// </summary>
public class AuthResponse
{
    /// <summary>
    /// The API key issued by the server for subsequent authenticated requests.
    /// </summary>
    [JsonProperty("apikey")]
    public string? ApiKey { get; set; }
}
