using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Server;

/// <summary>
/// Finds the server's <c>media-sessions</c> feature on Shoko's own
/// <c>GET /api/v3/Plugin/Features</c>. The feature's name is the only
/// thing this side knows: the hub and REST paths come from its metadata,
/// so no route of the media session plugin is written down here.
/// </summary>
public static class MediaSessionDetection
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>The feature the server advertises media sessions under.</summary>
    internal const string FeatureName = "media-sessions";

    /// <summary>
    /// The major version of the feature's contract this build reads. A
    /// different major means a different metadata shape.
    /// </summary>
    internal const int FeatureMajor = 1;

    /// <summary>Shoko's own endpoint listing every plugin's features.</summary>
    private const string FeaturesPath = "/api/v3/Plugin/Features";

    /// <summary>
    /// Ask a server whether it serves media sessions, and where.
    /// </summary>
    /// <param name="http">The client to ask with.</param>
    /// <param name="baseUrl">The server base URL.</param>
    /// <param name="apiKey">The API key, or <c>null</c> to ask anonymously.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The feature, or <c>null</c> when the server does not advertise one
    /// this build can read, or cannot be asked.
    /// </returns>
    public static async Task<MediaSessionsFeature?> DetectAsync(
        HttpClient http, string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}{FeaturesPath}");
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Add("apikey", apiKey);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Debug("Plugin features answered HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Find(JsonConvert.DeserializeObject<List<PluginFeatureDto>>(json) ?? []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.Debug(ex, "Media session detection failed");
            return null;
        }
    }

    /// <summary>
    /// The <c>media-sessions</c> feature out of a features listing, or
    /// <c>null</c> when there is none this build can read: absent, of
    /// another major version, or without a hub or API path.
    /// </summary>
    /// <param name="features">What the server listed.</param>
    /// <returns>The feature, or <c>null</c>.</returns>
    internal static MediaSessionsFeature? Find(IEnumerable<PluginFeatureDto> features)
    {
        var feature = features.FirstOrDefault(f => f.Name == FeatureName);
        if (feature is null)
            return null;

        if (!Version.TryParse(feature.Version, out var version) || version.Major != FeatureMajor)
        {
            Logger.Warn(
                "Server advertises media sessions at version {Version}, but this build reads {Major}.x",
                feature.Version,
                FeatureMajor);
            return null;
        }

        var hubPath = feature.Metadata?.Value<string>("HubPath");
        var apiPath = feature.Metadata?.Value<string>("ApiPath");
        if (string.IsNullOrEmpty(hubPath) || string.IsNullOrEmpty(apiPath))
        {
            Logger.Warn("Server advertises media sessions without a hub or API path");
            return null;
        }

        return new MediaSessionsFeature
        {
            PluginId = feature.PluginID,
            HubPath = hubPath,
            ApiPath = apiPath.TrimEnd('/'),
        };
    }
}
