namespace Shoko.Companion.Server;

/// <summary>
/// Outcome of an interactive username/password login attempt against a Shoko server.
/// Captures enough detail for callers to distinguish success, an authenticated
/// response without a key, an HTTP failure, and a transport error.
/// </summary>
/// <param name="ApiKey">The API key returned on success, or null.</param>
/// <param name="StatusCode">The HTTP status code of the auth response, or null if the request threw.</param>
/// <param name="Error">The exception message if the request threw, or null.</param>
public sealed record AuthAttemptResult(string? ApiKey, int? StatusCode, string? Error)
{
    /// <summary>True when a non-empty API key was obtained.</summary>
    public bool HasApiKey => ApiKey is { Length: > 0 };

    /// <summary>True when the server returned a 2xx response (regardless of whether a key was present).</summary>
    public bool ResponseSucceeded => StatusCode is >= 200 and < 300;
}
