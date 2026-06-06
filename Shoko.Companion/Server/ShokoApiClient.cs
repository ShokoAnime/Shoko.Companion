using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using NLog;
using Newtonsoft.Json;
using Shoko.Companion.Configuration;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Server;

/// <inheritdoc cref="IShokoApiClient" />
public class ShokoApiClient : IShokoApiClient
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly HttpClient _httpClient;

    private string _baseUrl = string.Empty;

    /// <inheritdoc />
    public string? ApiKey { get; private set; }

    /// <inheritdoc />
    public bool? LastResponseWasUnauthorized { get; private set; }

    /// <summary>
    /// Set or replace the API key used for subsequent authenticated requests.
    /// </summary>
    public void SetApiKey(string key) => ApiKey = key;

    /// <summary>
    /// Set the server base URL used for API requests.
    /// Must be set before making any API calls.
    /// </summary>
    public void SetBaseUrl(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

    /// <summary>
    /// Initializes a new instance of the <see cref="ShokoApiClient" /> class.
    /// </summary>
    public ShokoApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc />
    public async Task<bool> AuthenticateAsync(string username, string password, string device,
        CancellationToken ct = default)
    {
        var request = new AuthRequest
        {
            User = username,
            Pass = password,
            Device = device
        };

        var json = JsonConvert.SerializeObject(request);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync($"{_baseUrl}/api/auth", content, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn("Authentication failed with status code {StatusCode}", (int)response.StatusCode);
                return false;
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var authResponse = JsonConvert.DeserializeObject<AuthResponse>(responseJson);

            if (authResponse?.ApiKey is { Length: > 0 })
            {
                ApiKey = authResponse.ApiKey;
                Logger.Info("Successfully authenticated and obtained API key");
                return true;
            }

            Logger.Warn("Authentication response did not contain an API key");
            return false;
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("Authentication request was cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Authentication request failed");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<List<PlaylistItemDto>?> FetchPlaylistJsonAsync(string jsonUrl, CancellationToken ct = default)
    {
        LastResponseWasUnauthorized = null;
        var url = AppendApiKey(jsonUrl);

        try
        {
            var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                LastResponseWasUnauthorized = true;

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn("FetchPlaylistJson failed with status code {StatusCode}",
                    (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<List<PlaylistItemDto>>(json);
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("FetchPlaylistJson request was cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to fetch playlist JSON");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<VideoUserDataDto?> FetchFileUserDataAsync(int fileId, CancellationToken ct = default)
    {
        LastResponseWasUnauthorized = null;
        var url = AppendApiKey($"{_baseUrl}/api/v3/File/{fileId}/UserData");

        try
        {
            var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                LastResponseWasUnauthorized = true;

            if (!response.IsSuccessStatusCode)
            {
                // 404 means the file has no user data yet — not an error
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                Logger.Warn("FetchFileUserData for file {FileId} failed with status code {StatusCode}",
                    fileId, (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<VideoUserDataDto>(json);
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("FetchFileUserData request for file {FileId} was cancelled", fileId);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to fetch file user data for file {FileId}", fileId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> PutFileUserDataAsync(int fileId, VideoUserDataDto data, CancellationToken ct = default)
    {
        LastResponseWasUnauthorized = null;
        var url = AppendApiKey($"{_baseUrl}/api/v3/File/{fileId}/UserData");

        try
        {
            var json = JsonConvert.SerializeObject(data);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                LastResponseWasUnauthorized = true;

            if (response.IsSuccessStatusCode)
            {
                Logger.Debug("PutFileUserData for file {FileId} succeeded", fileId);
                return true;
            }

            Logger.Warn("PutFileUserData for file {FileId} failed with status code {StatusCode}",
                fileId, (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("PutFileUserData request for file {FileId} was cancelled", fileId);
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to put file user data for file {FileId}", fileId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ScrobbleAsync(int fileId, ScrobbleEventType eventType, TimeSpan? resumePosition = null,
        bool? watched = null, CancellationToken ct = default)
    {
        LastResponseWasUnauthorized = null;
        var url = $"{_baseUrl}/api/v3/File/{fileId}/Scrobble?event={eventType}";

        if (resumePosition.HasValue)
            url += $"&resumePosition={resumePosition.Value:hh\\:mm\\:ss\\.ffff}";

        if (watched.HasValue)
            url += $"&watched={watched.Value.ToString().ToLowerInvariant()}";

        url = AppendApiKey(url);

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, url);
            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                LastResponseWasUnauthorized = true;

            if (response.IsSuccessStatusCode)
            {
                Logger.Debug("Scrobble '{Event}' for file {FileId} succeeded", eventType, fileId);
                return true;
            }

            Logger.Warn("Scrobble '{Event}' for file {FileId} failed with status code {StatusCode}",
                eventType, fileId, (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("Scrobble request for file {FileId} was cancelled", fileId);
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Scrobble request failed for file {FileId}", fileId);
            return false;
        }
    }

    /// <inheritdoc />
    public string BuildStreamUrl(int fileId)
    {
        return AppendApiKey($"{_baseUrl}/api/v3/File/{fileId}/Stream");
    }

    /// <inheritdoc />
    public async Task<List<ManagedFolderDto>?> FetchManagedFoldersAsync(CancellationToken ct = default)
    {
        LastResponseWasUnauthorized = null;
        var url = AppendApiKey($"{_baseUrl}/api/v3/ManagedFolder");

        try
        {
            var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                LastResponseWasUnauthorized = true;

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn("FetchManagedFolders failed with status code {StatusCode}",
                    (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var folders = JsonConvert.DeserializeObject<List<ManagedFolderDto>>(json);

            // Cache per connection so the ManageFoldersDialog can display server-side info
            if (folders is not null)
            {
                var routeKey = RouteResolver.ExtractRouteKey(_baseUrl)
                    ?? (string.IsNullOrWhiteSpace(_baseUrl) ? "localhost:8111" : _baseUrl.TrimEnd('/'));
                var useHttps = _baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase);
                var conn = SettingsProvider.Instance.Settings.GetOrCreateConnectionByRouteKey(routeKey, useHttps);
                conn.CachedManagedFolders = folders;
                SettingsProvider.Instance.Save();
            }

            return folders;
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("FetchManagedFolders request was cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to fetch managed folders");
            return null;
        }
    }

    /// <summary>
    /// Set or replace the API key query parameter on a URL.
    /// </summary>
    private string AppendApiKey(string url)
    {
        if (string.IsNullOrEmpty(ApiKey))
            return url;

        var builder = new UriBuilder(url);
        var query = HttpUtility.ParseQueryString(builder.Query);
        query["apikey"] = ApiKey;
        builder.Query = query.ToString();
        return builder.ToString();
    }
}
