using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Shoko.Companion.Configuration;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Server;

/// <summary>
/// Helper for the interactive username/password login flow shared by the
/// connection and credential dialogs. Exchanges credentials for an API key
/// via the Shoko <c>/api/auth</c> endpoint.
/// </summary>
public static class ShokoAuth
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Attempt to log in to <paramref name="baseUrl"/> with the given credentials.
    /// Never throws; transport failures are reported via <see cref="AuthAttemptResult.Error"/>.
    /// </summary>
    /// <param name="baseUrl">Full base URL including protocol (e.g. http://myserver:8111).</param>
    /// <param name="username">Shoko username.</param>
    /// <param name="password">Shoko password.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public static async Task<AuthAttemptResult> LoginAsync(string baseUrl, string username, string password,
        CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = LoginTimeout };
            var body = JsonConvert.SerializeObject(new AuthRequest
            {
                User = username,
                Pass = password,
                Device = DeviceInfo.DeviceName
            });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{baseUrl}/api/auth", content, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return new AuthAttemptResult(null, (int)resp.StatusCode, null);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var authResp = JsonConvert.DeserializeObject<AuthResponse>(json);
            return new AuthAttemptResult(authResp?.ApiKey, (int)resp.StatusCode, null);
        }
        catch (Exception ex)
        {
            return new AuthAttemptResult(null, null, ex.Message);
        }
    }
}
