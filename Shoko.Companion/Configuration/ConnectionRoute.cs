using Newtonsoft.Json;

namespace Shoko.Companion.Configuration;

/// <summary>
/// A single route for reaching a Shoko Server.
/// Stores the host:port[/subpath] separately from the protocol
/// so route lookups from incoming URLs can match on BaseUrl alone
/// (since the incoming URL may or may not carry a protocol).
/// </summary>
public class ConnectionRoute
{
    /// <summary>
    /// Host, port, and optional sub-path (e.g. <c>myserver:8111</c> or <c>192.168.1.5:8111/virtual/client</c>).
    /// No protocol prefix.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether to connect via HTTPS. If false, HTTP is used.
    /// Once set by initial protocol probing, this is persisted and trusted.
    /// </summary>
    public bool UseHttps { get; set; }

    /// <summary>
    /// The full URL constructed from <see cref="BaseUrl"/> and <see cref="UseHttps"/>.
    /// </summary>
    [JsonIgnore]
    public string FullUrl => $"{(UseHttps ? "https" : "http")}://{BaseUrl.TrimEnd('/')}";

    /// <summary>
    /// Returns the display representation (FullUrl).
    /// </summary>
    public override string ToString() => FullUrl;
}
