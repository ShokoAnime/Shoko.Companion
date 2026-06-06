using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Server;

/// <summary>
/// Client interface for communicating with the Shoko Server REST API.
/// Provides methods for authentication, fetching playlists, retrieving user data,
/// scrobbling playback events, and building stream URLs.
/// </summary>
public interface IShokoApiClient
{
    /// <summary>
    /// Set or update the API key used for authenticated requests.
    /// </summary>
    void SetApiKey(string key);

    /// <summary>
    /// Set the server base URL used for API requests.
    /// Must be set before making any API calls.
    /// </summary>
    void SetBaseUrl(string baseUrl);

    /// <summary>
    /// Gets the current API key used for authenticated requests.
    /// </summary>
    string? ApiKey { get; }

    /// <summary>
    /// Exchange user+pass for an API key. Sets ApiKey on success.
    /// </summary>
    Task<bool> AuthenticateAsync(string username, string password, string device, CancellationToken ct = default);

    /// <summary>
    /// Fetch the JSON playlist from the Generate endpoint (strip .m3u8 from the URL).
    /// </summary>
    Task<List<PlaylistItemDto>?> FetchPlaylistJsonAsync(string jsonUrl, CancellationToken ct = default);

    /// <summary>
    /// Get per-user file data (resume position, watched state).
    /// </summary>
    Task<VideoUserDataDto?> FetchFileUserDataAsync(int fileId, CancellationToken ct = default);

    /// <summary>
    /// Persist user data (progress position, stream selections) for a file via PUT.
    /// </summary>
    Task<bool> PutFileUserDataAsync(int fileId, VideoUserDataDto data, CancellationToken ct = default);

    /// <summary>
    /// Send a scrobble event to the server.
    /// </summary>
    Task<bool> ScrobbleAsync(int fileId, ScrobbleEventType eventType, TimeSpan? resumePosition = null, bool? watched = null, CancellationToken ct = default);

    /// <summary>
    /// Build the stream URL for a file, including the apikey.
    /// </summary>
    string BuildStreamUrl(int fileId);

    /// <summary>
    /// Fetch the list of managed (import) folders from the Shoko server.
    /// Returns null on failure.
    /// </summary>
    Task<List<ManagedFolderDto>?> FetchManagedFoldersAsync(CancellationToken ct = default);

    /// <summary>
    /// True when the most recent API response was HTTP 401 Unauthorized.
    /// Reset to null at the start of each API call.
    /// </summary>
    bool? LastResponseWasUnauthorized { get; }
}
