using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Shoko.Companion.Configuration;

namespace Shoko.Companion.Tests;

/// <summary>
///   Just enough of a Shoko server on loopback for a <c>shoko://</c> URL to
///   resolve the way it does in the app: <c>Init/Version</c> answers, so the
///   route resolver takes the address as it is, and <c>Generate.m3u8</c>
///   serves a playlist naming the video asked for. A connection holding an
///   API key is added to the settings for its address, and removed again on
///   dispose, so no credential prompt is ever reached.
///
///   <para>
///     The JSON half of a playlist is not served here: that goes through
///     <c>IShokoApiClient</c>, which the tests mock.
///   </para>
/// </summary>
internal sealed class FakeShokoServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ServerConnection _connection;

    public FakeShokoServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = ServeAsync();

        _connection = new ServerConnection
        {
            Name = $"fake-{Port}",
            Routes = [new ConnectionRoute { BaseUrl = $"127.0.0.1:{Port}", UseHttps = false }],
            ApiKey = "apikey",
        };
        SettingsProvider.Instance.Settings.Connections.Add(_connection);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>The <c>shoko://</c> URL that plays one video.</summary>
    public string ShokoUrl(int videoId) => $"shoko://127.0.0.1:{Port}/play?playlist=f{videoId}";

    /// <summary>The APIv3 stream URL of a video, as mpv's playlist names it.</summary>
    public string StreamUrl(int videoId) => $"{BaseUrl}/api/v3/File/{videoId}/Stream";

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                var path = context.Request.Url!.AbsolutePath;
                string body;
                if (path.EndsWith("/api/v3/Init/Version", StringComparison.Ordinal))
                {
                    body = "{}";
                }
                else if (path.EndsWith("/api/v3/Playlist/Generate.m3u8", StringComparison.Ordinal)
                    && int.TryParse(context.Request.QueryString["playlist"]?.TrimStart('f'), out var videoId))
                {
                    body = $"#EXTM3U\n#EXTINF:-1,Episode {videoId}\n{StreamUrl(videoId)}\n";
                }
                else
                {
                    context.Response.StatusCode = 404;
                    body = string.Empty;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception)
            {
                // A test that has finished may hang up first.
            }
        }
    }

    public void Dispose()
    {
        SettingsProvider.Instance.Settings.Connections.Remove(_connection);
        ServerConnection.ClearReachabilityCache();
        _listener.Stop();
        _listener.Close();
    }
}
