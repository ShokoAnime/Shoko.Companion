using Newtonsoft.Json;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO containing the API key returned from a successful authentication.
/// </summary>
public class AuthResponseDto
{
    /// <summary>The API key for subsequent requests.</summary>
    [JsonProperty("apikey")]
    public string? ApiKey { get; set; }
}
