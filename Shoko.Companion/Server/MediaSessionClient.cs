using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Playback;

namespace Shoko.Companion.Server;

/// <summary>
/// Client for the Shoko Media Session plugin's SignalR hub.
/// Connects, registers as a companion session, relays commands to the
/// playback coordinator, and reports playback state.
/// </summary>
public sealed class MediaSessionClient : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _deviceName;
    private readonly IPlaybackCoordinator _coordinator;
    private HubConnection? _connection;
    private Guid? _sessionId;
    private PlaybackStateUpdateDto? _lastState;
    private CancellationTokenSource? _stoppedTimerCts;
    private static readonly TimeSpan StoppedToIdleDelay = TimeSpan.FromSeconds(10);
    private bool _hasActivePlayback;

    /// <summary>
    ///   Whether a media file is currently loaded and playable.
    ///   Used to gate resume/pause/seek/stop/screenshot capabilities.
    /// </summary>
    public bool HasActivePlayback
    {
        set
        {
            if (_hasActivePlayback != value)
            {
                _hasActivePlayback = value;
                _ = UpdateCapabilitiesOnHubAsync();
            }
        }
    }

    /// <summary>
    /// Raised when the connection state changes.
    /// </summary>
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>
    /// The display name of the registered session.
    /// </summary>
    public string SessionName => _deviceName;

    /// <summary>
    /// Whether the client is currently connected to the hub.
    /// </summary>
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSessionClient"/> class.
    /// </summary>
    /// <param name="baseUrl">The server base URL (e.g. <c>http://myserver:8111</c>).</param>
    /// <param name="apiKey">The API key for authentication.</param>
    /// <param name="deviceName">The device name to register with the hub.</param>
    /// <param name="coordinator">The playback coordinator to relay commands to.</param>
    /// <param name="initialState">
    ///   Optional. The device's current playback state. When provided, the
    ///   session starts in this state instead of defaulting to
    ///   <c>Idle</c>. Useful when reconnecting after a full client reset.
    /// </param>
    public MediaSessionClient(
        string baseUrl,
        string apiKey,
        string deviceName,
        IPlaybackCoordinator coordinator,
        PlaybackStateUpdateDto? initialState = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _deviceName = deviceName;
        _coordinator = coordinator;
        _lastState = initialState;
        _hasActivePlayback = initialState?.State is "Playing" or "Paused";
    }

    /// <summary>
    /// Probe the server to check if the Media Session plugin is available.
    /// </summary>
    /// <param name="baseUrl">The server base URL.</param>
    /// <param name="apiKey">The API key for authentication.</param>
    /// <returns><c>true</c> if the plugin endpoint responds with success.</returns>
    public static async Task<bool> IsPluginAvailableAsync(string baseUrl, string apiKey)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = $"{baseUrl.TrimEnd('/')}/api/plugin/MediaSession/v1/Available";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", apiKey);

            using var response = await http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Media Session plugin availability check failed");
            return false;
        }
    }

    /// <summary>
    /// Connect to the hub and register as a companion session.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (_connection is not null)
            await DisposeAsync();

        _connection = new HubConnectionBuilder()
            .WithUrl($"{_baseUrl}/signalr/plugin/MediaSession/v1", options =>
            {
                options.Headers["apikey"] = _apiKey;
            })
            .WithAutomaticReconnect(new RetryDelayProvider())
            .Build();

        // Wire up server-to-client methods
        _connection.On<PlaybackRequestDto>("Play", async request =>
        {
            Logger.Info("MediaSession: Play command received");
            await HandlePlayAsync(request);
        });

        _connection.On("Pause", async () =>
        {
            Logger.Info("MediaSession: Pause command received");
            await _coordinator.PauseAsync();
        });

        _connection.On("Resume", async () =>
        {
            Logger.Info("MediaSession: Resume command received");
            await _coordinator.ResumeAsync();
        });

        _connection.On<TimeSpan>("Seek", async position =>
        {
            Logger.Info("MediaSession: Seek to {Position}", position);
            await _coordinator.SeekAsync(position);
        });

        _connection.On("Stop", async () =>
        {
            Logger.Info("MediaSession: Stop command received");
            await _coordinator.StopAsync();
        });

        _connection.On<int?, bool?>("SetVolume", async (volume, muted) =>
        {
            Logger.Info("MediaSession: SetVolume command received (volume={Volume}, muted={Muted})",
                volume, muted);
            try
            {
                await _coordinator.SetVolumeAsync(volume, muted);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MediaSession: Failed to handle SetVolume command");
            }
        });

        _connection.On<ScreenshotRequestDto>("RequestScreenshot", async request =>
        {
            Logger.Info("MediaSession: Screenshot requested (position={Position})", request.Position);
            try
            {
                var data = await _coordinator.CaptureScreenshotAsync(request.Position);
                if (data is not null)
                {
                    await _connection.InvokeAsync("ReportScreenshot", request.RequestId,
                        new CaptureResultDto { Data = data });
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "MediaSession: Failed to report screenshot");
            }
        });

        _connection.Closed += async error =>
        {
            Logger.Warn(error, "MediaSession: Connection closed");
            ConnectionStateChanged?.Invoke(false);
            var _ = _coordinator.ShowOsdTextAsync("Disconnected from Media Session API");
        };

        _connection.Reconnecting += _ =>
        {
            Logger.Info("MediaSession: Reconnecting...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            Logger.Info("MediaSession: Reconnected, reconnecting session...");
            await ReconnectSessionOrRegisterAsync();
            ConnectionStateChanged?.Invoke(true);
            var __ = _coordinator.ShowOsdTextAsync("Reconnected to Media Session API");
        };

        try
        {
            await _connection.StartAsync().ConfigureAwait(false);
            Logger.Info("MediaSession: Connected to hub");
            await ReconnectSessionOrRegisterAsync();
            ConnectionStateChanged?.Invoke(true);
            var _ = _coordinator.ShowOsdTextAsync("Connected to Media Session API");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "MediaSession: Failed to connect");
        }
    }

    /// <summary>
    /// Handle a Play command from the hub.
    /// </summary>
    private async Task HandlePlayAsync(PlaybackRequestDto request)
    {
        try
        {
            // Construct a shoko:// URL — the coordinator handles the
            // full resolution pipeline (playlist, mpv, scrobble).
            var uri = new Uri(_baseUrl);
            var shokoUrl = $"shoko://{uri.Authority}/play?playlist=f{request.VideoId}";
            await _coordinator.PlayAsync(shokoUrl, request.StartPosition, request.Append);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MediaSession: Failed to handle Play command");
        }
    }

    /// <summary>
    /// Try to reconnect to the previous session, or register a new one.
    /// </summary>
    private async Task ReconnectSessionOrRegisterAsync()
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected)
            return;

        // If we have a previous session ID, try to reclaim it
        if (_sessionId.HasValue)
        {
            try
            {
                var result = await _connection.InvokeAsync<SessionInfoDto>(
                    "ReconnectSession", _sessionId.Value, _lastState);
                _sessionId = result.SessionId;
                Logger.Info("MediaSession: Reconnected to session {SessionId}", _sessionId.Value);

                // Push current capabilities — they may have changed while
                // disconnected (e.g. playback stopped, _hasActivePlayback flipped).
                await UpdateCapabilitiesOnHubAsync();

                // Restart stopped→idle timer if we reconnected while stopped,
                // otherwise the hub would see "Stopped" indefinitely.
                if (_lastState?.State == "Stopped")
                    StartStoppedTimer();

                return;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "MediaSession: Reconnect failed, registering new session");
            }
        }

        await RegisterSessionAsync();

        // Same restart for the fresh-registration path
        if (_lastState?.State == "Stopped")
            StartStoppedTimer();
    }

    /// <summary>
    /// Register this companion as a session on the hub.
    /// </summary>
    private async Task RegisterSessionAsync()
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected)
            return;

        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
            var deviceInfo = new RegisterDeviceDto
            {
                Name = _deviceName,
                ClientName = "Shoko Desktop Companion",
                HostName = Environment.MachineName,
                DeviceType = "Companion",
                Platform = GetPlatform(),
                Version = version,
                Capabilities = BuildCurrentCapabilities(),
            };

            var result = await _connection.InvokeAsync<SessionInfoDto>("RegisterSession", deviceInfo, _lastState);
            _sessionId = result.SessionId;
            Logger.Info("MediaSession: Registered as session {SessionId}", _sessionId.Value);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "MediaSession: Failed to register session");
        }
    }

    /// <summary>
    /// Report playback state to the hub.
    /// </summary>
    /// <param name="state">The current playback state to report.</param>
    public async Task ReportStateAsync(PlaybackStateUpdateDto state)
    {
        CancelStoppedTimer();
        _lastState = state;

        if (_connection is null || _connection.State != HubConnectionState.Connected || _sessionId is null)
            return;

        try
        {
            await _connection.InvokeAsync("UpdateState", state);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "MediaSession: Failed to report state");
        }

        // If playback just stopped, schedule an auto-transition to Idle
        // after a grace period. Any new play/pause/resume cancels it.
        if (state.State == "Stopped")
            StartStoppedTimer();
    }

    /// <summary>
    /// Start the stopped→idle timer. Any previous timer is cancelled first.
    /// </summary>
    private void StartStoppedTimer()
    {
        CancelStoppedTimer();
        Logger.Trace("MediaSession: Stopped→Idle timer started ({0}s)", StoppedToIdleDelay.TotalSeconds);
        var cts = new CancellationTokenSource();
        _stoppedTimerCts = cts;
        _ = StoppedToIdleAsync(cts.Token);
    }

    /// <summary>
    /// Cancel any pending stopped→idle transition.
    /// </summary>
    private void CancelStoppedTimer()
    {
        _stoppedTimerCts?.Cancel();
        _stoppedTimerCts?.Dispose();
        _stoppedTimerCts = null;
    }

    /// <summary>
    /// After <see cref="StoppedToIdleDelay"/> report Idle to the hub.
    /// Cancelled if a new state update arrives before the timeout.
    /// </summary>
    private async Task StoppedToIdleAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StoppedToIdleDelay, ct);
            Logger.Trace("MediaSession: Stopped→Idle timer fired — reporting Idle");
            await ReportStateAsync(new PlaybackStateUpdateDto
            {
                State = "Idle",
                VideoId = null,
                Title = null,
                MediaType = null,
                Position = TimeSpan.Zero,
                Duration = null,
                StreamUrl = null,
                IsPaused = false,
            });
        }
        catch (OperationCanceledException)
        {
            Logger.Trace("MediaSession: Stopped→Idle timer cancelled — new state arrived");
        }
    }

    /// <summary>
    ///   Build current capability flags from settings + playback state.
    /// </summary>
    private SessionCapabilitiesDto BuildCurrentCapabilities()
    {
        var s = SettingsProvider.Instance.Settings;
        var privacyOverrideControl = s.EffectivePrivacyMode && s.PrivacyModeDisableRemoteControl;
        var privacyOverrideScreenshot = s.EffectivePrivacyMode && s.PrivacyModeDisableRemoteScreenshots;

        return new SessionCapabilitiesDto
        {
            CanPlay = s.AllowRemotePlay && !privacyOverrideControl,
            CanResumeOrPause = _hasActivePlayback && !privacyOverrideControl,
            CanSeek = _hasActivePlayback && !privacyOverrideControl,
            CanStop = _hasActivePlayback && !privacyOverrideControl,
            CanReportState = _hasActivePlayback,
            CanCaptureScreenshot = s.AllowRemoteScreenshot && _hasActivePlayback && !privacyOverrideScreenshot,
            CanScreenshotAtPosition = s.AllowRemoteScreenshot && _hasActivePlayback && !privacyOverrideScreenshot,
            MaxVolume = PlaybackCoordinator.MaxMpvVolume,
            CanSetVolume = s.AllowRemoteVolumeControl && !privacyOverrideControl,
        };
    }

    /// <summary>
    ///   Build current capability flags from settings + playback state
    ///   and push them to the hub so the dashboard reacts immediately.
    /// </summary>
    public async Task UpdateCapabilitiesOnHubAsync()
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected || _sessionId is null)
            return;

        var caps = BuildCurrentCapabilities();

        try
        {
            await _connection.InvokeAsync("UpdateCapabilities", caps);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "MediaSession: Failed to update capabilities");
        }
    }

    /// <summary>
    /// Get the current platform string for device registration.
    /// </summary>
    private static string GetPlatform()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        CancelStoppedTimer();

        if (_connection is not null)
        {
            try
            {
                // Try to unregister before disconnecting
                if (_connection.State == HubConnectionState.Connected && _sessionId.HasValue)
                    await _connection.InvokeAsync("UnregisterSession");
            }
            catch
            {
                // Best-effort
            }

            await _connection.DisposeAsync();
            _connection = null;
            _sessionId = null;
        }
    }

    // ── DTOs matching the plugin's hub contract ──

    private sealed class RegisterDeviceDto
    {
        [JsonProperty("Name")]
        public string Name { get; init; } = string.Empty;

        [JsonProperty("DeviceType")]
        public string DeviceType { get; init; } = string.Empty;

        [JsonProperty("ClientName")]
        public string? ClientName { get; init; }

        [JsonProperty("HostName")]
        public string? HostName { get; init; }

        [JsonProperty("Platform")]
        public string? Platform { get; init; }

        [JsonProperty("Version")]
        public string? Version { get; init; }

        [JsonProperty("Capabilities")]
        public SessionCapabilitiesDto Capabilities { get; init; } = new();
    }

    private sealed class SessionCapabilitiesDto
    {
        [JsonProperty("CanPlay")]
        public bool CanPlay { get; init; } = true;

        [JsonProperty("CanResumeOrPause")]
        public bool CanResumeOrPause { get; init; } = true;

        [JsonProperty("CanSeek")]
        public bool CanSeek { get; init; } = true;

        [JsonProperty("CanStop")]
        public bool CanStop { get; init; } = true;

        [JsonProperty("CanReportState")]
        public bool CanReportState { get; init; } = true;

        [JsonProperty("CanCaptureScreenshot")]
        public bool CanCaptureScreenshot { get; init; } = false;

        [JsonProperty("CanScreenshotAtPosition")]
        public bool CanScreenshotAtPosition { get; init; } = false;

        [JsonProperty("MaxVolume")]
        public int? MaxVolume { get; init; }

        [JsonProperty("CanSetVolume")]
        public bool CanSetVolume { get; init; } = false;
    }

    private sealed class SessionInfoDto
    {
        [JsonProperty("SessionId")]
        public Guid SessionId { get; init; }
    }

    /// <summary>
    /// Reconnect delay provider: 0s, 2s, 10s, 30s, then every 60s.
    /// </summary>
    private sealed class RetryDelayProvider : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            return retryContext.PreviousRetryCount switch
            {
                0 => TimeSpan.Zero,
                1 => TimeSpan.FromSeconds(2),
                2 => TimeSpan.FromSeconds(10),
                3 => TimeSpan.FromSeconds(30),
                _ => TimeSpan.FromSeconds(60),
            };
        }
    }
}

/// <summary>
/// DTO for commands from the hub, mirroring the server's PlaybackRequest.
/// </summary>
public sealed class PlaybackRequestDto
{
    /// <summary>
    /// Shoko video ID to play. The companion resolves this to a stream URL
    /// through the playlist/stream pipeline.
    /// </summary>
    [JsonProperty("VideoId")]
    public int VideoId { get; init; }

    /// <summary>
    ///   Optional. Weather to append to the current playlist, or replace it.
    ///   Leave as <c>null</c> to leave it up to the client. Set to <c>true</c>
    ///   to always append, <c>false</c> to always replace.
    /// </summary>
    [JsonProperty("Append")]
    public bool? Append { get; init; }

    /// <summary>
    ///   Optional. Start position to seek to upon playing the video. The
    ///   companion seeks to this position after loading the video.
    /// </summary>
    [JsonProperty("StartPosition")]
    public TimeSpan? StartPosition { get; init; }
}

/// <summary>
/// DTO for reporting state to the hub, mirroring the server's PlaybackStateUpdate.
/// </summary>
public sealed class PlaybackStateUpdateDto
{
    /// <summary>
    /// The playback state string (Playing, Paused, Idle, Stopped, Loading, Error).
    /// </summary>
    [JsonProperty("State")]
    public string State { get; init; } = "Idle";

    /// <summary>
    /// The Shoko video ID, if managed by Shoko.
    /// </summary>
    [JsonProperty("VideoId")]
    public int? VideoId { get; init; }

    /// <summary>
    /// Title of the currently playing media.
    /// </summary>
    [JsonProperty("Title")]
    public string? Title { get; init; }

    /// <summary>
    /// Media type: "video", "audio", or "unknown".
    /// </summary>
    [JsonProperty("MediaType")]
    public string? MediaType { get; init; }

    /// <summary>
    /// Thumbnail or poster URL, if available.
    /// </summary>
    [JsonProperty("ThumbnailUrl")]
    public string? ThumbnailUrl { get; init; }

    /// <summary>
    /// Next item in the play queue, or null if none.
    /// </summary>
    [JsonProperty("NextItem")]
    public NextMediaItemInfoDto? NextItem { get; init; }

    /// <summary>
    /// The current playback position.
    /// </summary>
    [JsonProperty("Position")]
    public TimeSpan Position { get; init; }

    /// <summary>
    /// The total duration, if known.
    /// </summary>
    [JsonProperty("Duration")]
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Whether playback is currently paused.
    /// </summary>
    [JsonProperty("IsPaused")]
    public bool IsPaused { get; init; }

    /// <summary>
    /// The current volume (percent, 0–130), or null if unknown.
    /// </summary>
    [JsonProperty("Volume")]
    public int? Volume { get; init; }

    /// <summary>
    /// Whether audio is muted, or null if unknown.
    /// </summary>
    [JsonProperty("IsMuted")]
    public bool? IsMuted { get; init; }

    /// <summary>
    /// Stream URL the player is using, if known.
    /// </summary>
    [JsonProperty("StreamUrl")]
    public string? StreamUrl { get; init; }
}

/// <summary>
/// DTO for the next item in the play queue, mirroring the server's NextMediaItemInfo.
/// </summary>
public sealed class NextMediaItemInfoDto
{
    /// <summary>
    /// Human-readable title, or null if no next item.
    /// </summary>
    [JsonProperty("Title")]
    public string? Title { get; init; }

    /// <summary>
    /// Media type hint: "video" or "audio". Null if unknown.
    /// </summary>
    [JsonProperty("MediaType")]
    public string? MediaType { get; init; }

    /// <summary>
    /// Shoko video ID, if the next item is managed by Shoko.
    /// </summary>
    [JsonProperty("VideoId")]
    public int? VideoId { get; init; }

    /// <summary>
    /// Thumbnail or poster URL for the next item, if available.
    /// </summary>
    [JsonProperty("ThumbnailUrl")]
    public string? ThumbnailUrl { get; init; }
}

/// <summary>
/// DTO for a screenshot request from the hub, mirroring the server's ScreenshotRequest.
/// </summary>
public sealed class ScreenshotRequestDto
{
    /// <summary>
    /// Unique request identifier for correlating the response.
    /// </summary>
    [JsonProperty("RequestId")]
    public Guid RequestId { get; init; }

    /// <summary>
    /// Optional seek position. When set, the companion seeks to this position
    /// before capturing (via a headless mpv slave). When null, captures the
    /// current frame from the active playback instance.
    /// </summary>
    [JsonProperty("Position")]
    public TimeSpan? Position { get; init; }
}

/// <summary>
/// DTO for screenshot capture results, mirroring the server's CaptureResult.
/// </summary>
public sealed class CaptureResultDto
{
    /// <summary>
    /// Raw image data bytes.
    /// </summary>
    [JsonProperty("Data")]
    public byte[] Data { get; init; } = [];

    /// <summary>
    /// Image format. One of "webp", "png", or "jpeg".
    /// Defaults to "png" (mpv's screenshot output format).
    /// </summary>
    [JsonProperty("Format")]
    public string Format { get; init; } = "png";
}
