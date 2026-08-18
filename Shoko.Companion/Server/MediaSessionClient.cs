using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Playback;
using Shoko.Companion.Server.Models;

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
    ///   The settings declaration the server last accepted, or <c>null</c>
    ///   when what it holds is unknown. Only ever what a successful send
    ///   carried, so a dropped push is retried by the next change.
    /// </summary>
    private SessionSettingsDto? _lastSentSettings;

    /// <summary>
    ///   The capability declaration the server last accepted, or
    ///   <c>null</c> when what it holds is unknown. Same discipline as
    ///   <see cref="_lastSentSettings"/>, and for the same reason: only a
    ///   successful send is recorded, so a dropped push is retried by the
    ///   next thing that recomputes.
    /// </summary>
    private SessionCapabilitiesDto? _lastSentCapabilities;

    /// <summary>
    ///   Whether a media file is currently loaded and playable.
    ///   Used to gate resume/pause/seek/stop/screenshot capabilities.
    /// </summary>
    public bool HasActivePlayback
    {
        set
        {
            _hasActivePlayback = value;
            _ = UpdateCapabilitiesOnHubAsync();
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

        // Every path that changes a setting ends in Save(), which raises
        // this — the settings window, the tray, the mpv privacy keybinding
        // and an edit made to settings.json by hand. Subscribing to the one
        // event rather than teaching each of those callers about the hub is
        // what makes "and on change" true of all of them rather than the
        // ones somebody remembered.
        SettingsProvider.Instance.SettingsChanged += OnSettingsChanged;
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

        _connection.On("SkipNext", async () =>
        {
            Logger.Info("MediaSession: SkipNext command received");
            await _coordinator.SkipNextAsync();
        });

        _connection.On("SkipPrevious", async () =>
        {
            Logger.Info("MediaSession: SkipPrevious command received");
            await _coordinator.SkipPreviousAsync();
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

        _connection.On<double>("SetPlaybackRate", async rate =>
        {
            Logger.Info("MediaSession: SetPlaybackRate command received (rate={Rate})", rate);
            try
            {
                await _coordinator.SetPlaybackRateAsync(rate);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MediaSession: Failed to handle SetPlaybackRate command");
            }
        });

        _connection.On<bool>("SetFullscreen", async isFullscreen =>
        {
            Logger.Info("MediaSession: SetFullscreen command received (isFullscreen={IsFullscreen})",
                isFullscreen);
            try
            {
                await _coordinator.SetFullscreenAsync(isFullscreen);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MediaSession: Failed to handle SetFullscreen command");
            }
        });

        _connection.On<PlaybackTrackSelectionDto>("SetTracks", async tracks =>
        {
            Logger.Info(
                "MediaSession: SetTracks command received "
                + "(video={Video}, audio={Audio}, subtitle={Subtitle})",
                tracks?.VideoOrdinal, tracks?.AudioOrdinal, tracks?.SubtitleIndex);
            try
            {
                if (tracks is not null)
                    await _coordinator.SetTracksAsync(tracks);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MediaSession: Failed to handle SetTracks command");
            }
        });

        _connection.On<string>("JumpToPlaylistItem", async streamUrl =>
        {
            Logger.Info("MediaSession: JumpToPlaylistItem command received");
            try
            {
                await _coordinator.JumpToPlaylistItemAsync(streamUrl);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MediaSession: Failed to handle JumpToPlaylistItem command");
            }
        });

        _connection.On<IReadOnlyList<PlaylistItemRequestDto>, int?>("AddToPlaylist", async (items, atIndex) =>
        {
            Logger.Info("MediaSession: AddToPlaylist command received ({Count} items at index {Index})",
                items?.Count ?? 0, atIndex);
            try
            {
                if (items is { Count: > 0 })
                {
                    // Each queued item is a play request in its own right, so
                    // resolve it to a shoko:// URL the same way the Play
                    // command does and carry its start position along; the
                    // coordinator handles the rest of the pipeline.
                    var uri = new Uri(_baseUrl);
                    var additions = items
                        .Select(item => new PlaylistAddition(
                            $"shoko://{uri.Authority}/play?playlist=f{item.VideoId}",
                            item.StartPosition))
                        .ToList();
                    await _coordinator.AddToPlaylistAsync(additions, atIndex);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MediaSession: Failed to handle AddToPlaylist command");
            }
        });

        _connection.On<IReadOnlyList<string>>("RemoveFromPlaylist", async streamUrls =>
        {
            Logger.Info("MediaSession: RemoveFromPlaylist command received");
            try
            {
                await _coordinator.RemoveFromPlaylistAsync(streamUrls);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MediaSession: Failed to handle RemoveFromPlaylist command");
            }
        });

        _connection.On<int, int>("MovePlaylistItem", async (fromIndex, toIndex) =>
        {
            Logger.Info("MediaSession: MovePlaylistItem command received ({From} -> {To})",
                fromIndex, toIndex);
            try
            {
                await _coordinator.MovePlaylistItemAsync(fromIndex, toIndex);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MediaSession: Failed to handle MovePlaylistItem command");
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
                    "ReconnectSession", _sessionId.Value, _lastState, _coordinator.CurrentPlaylist);
                SetSessionId(result.SessionId);
                Logger.Info("MediaSession: Reconnected to session {SessionId}", result.SessionId);

                // Push current capabilities — they may have changed while
                // disconnected (e.g. playback stopped, _hasActivePlayback
                // flipped). Forced for the same reason the settings push
                // below is: the reclaimed session holds whatever it was
                // given before the drop, and this client no longer knows
                // what that was.
                await UpdateCapabilitiesOnHubAsync(force: true);

                // And the settings, for the same reason and one more: a
                // viewer who turned privacy on while this client was off
                // the wire changed the one declaration whose whole point
                // is that the server acts on it. Forced, because the
                // reclaimed session's settings are whatever it held before
                // the drop and this client no longer knows what that was.
                await UpdateSettingsOnHubAsync(force: true);

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
                Capabilities = BuildCurrentCapabilities(_hasActivePlayback),
                Settings = BuildCurrentSettings(),
            };

            var result = await _connection.InvokeAsync<SessionInfoDto>(
                "RegisterSession", deviceInfo, _lastState, _coordinator.CurrentPlaylist);
            SetSessionId(result.SessionId);
            // Registration carried both declarations, so record them as
            // sent. Doing this only on success is what makes a failed
            // register followed by a re-register push them again rather
            // than conclude the server already has them.
            _lastSentSettings = deviceInfo.Settings;
            _lastSentCapabilities = deviceInfo.Capabilities;
            Logger.Info("MediaSession: Registered as session {SessionId}", result.SessionId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "MediaSession: Failed to register session");
        }
    }

    /// <summary>
    ///   Record the session id this companion is registered under, and push it
    ///   to the coordinator, which stamps it onto the media session stream URLs it
    ///   plays. Until this has run the coordinator holds <c>null</c> and falls
    ///   back to APIv3 — registration is asynchronous, and a playlist can be
    ///   fetched before the hub connects at all.
    /// </summary>
    /// <param name="sessionId">The session id, or <c>null</c> when we hold none.</param>
    private void SetSessionId(Guid? sessionId)
    {
        _sessionId = sessionId;
        _coordinator.MediaSessionId = sessionId;
        // A different session holds different declarations, and none at
        // all holds none. Forgetting here is what stops a fresh
        // registration from believing a previous session's declarations
        // still stand.
        _lastSentSettings = null;
        _lastSentCapabilities = null;
    }

    /// <summary>
    ///   Report playback state to the hub.
    ///
    ///   <para>
    ///     The capability declaration is brought up to date first, and
    ///     that ordering is the point rather than an accident. Several of
    ///     the flags are computed from things that move without anybody
    ///     saving a setting — what is loaded, and
    ///     <see cref="CompanionSettings.EffectivePrivacyMode"/>, which
    ///     turns itself on when restricted content starts — so pinning
    ///     the refresh to the one call that happens whenever anything
    ///     moves at all is what keeps the declaration from going stale in
    ///     a way no list of call sites can be trusted to cover. Unchanged
    ///     declarations cost nothing: the push compares before it sends.
    ///   </para>
    /// </summary>
    /// <param name="state">The current playback state to report.</param>
    public async Task ReportStateAsync(PlaybackStateUpdateDto state)
    {
        CancelStoppedTimer();
        _lastState = state;

        if (_connection is null || _connection.State != HubConnectionState.Connected || _sessionId is null)
            return;

        await UpdateCapabilitiesOnHubAsync();

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
    /// Report the full playlist to the hub, independent of state updates.
    /// </summary>
    /// <param name="items">
    ///   The full playlist items in order. Pass an empty list when idle.
    /// </param>
    public async Task ReportPlaylistAsync(IReadOnlyList<MediaItemInfoDto> items)
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected || _sessionId is null)
            return;

        try
        {
            await _connection.InvokeAsync("UpdatePlaylist", items);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "MediaSession: Failed to report playlist");
        }
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
                Position = TimeSpan.Zero,
                Duration = null,
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
    /// <param name="hasActivePlayback">
    ///   Whether a media file is currently loaded and playable.
    /// </param>
    internal static SessionCapabilitiesDto BuildCurrentCapabilities(bool hasActivePlayback)
    {
        var s = SettingsProvider.Instance.Settings;

        // Privacy does not appear below any more, and that is a change in
        // what this client *declares* rather than only in what it hides.
        // A capability answers "can this build do it", and "can, but the
        // viewer has privacy on right now" is a state — one the server is
        // now told directly, on SessionSettings. Once it knows, it refuses
        // control of a private item itself, at the session manager, across
        // eleven commands; a second copy of that rule here could only ever
        // disagree with the first, and the way it disagreed would be to
        // declare an ability away permanently for a state that passes.
        //
        // Every flag that carried the old `!privacyOverrideControl` was
        // re-read on removing it rather than stripped, and each is noted
        // below where the answer is not simply "the server guards this".
        //
        // Screenshots keep their own gate. That switch was not part of the
        // ruling that removed the control one, so it stays as it was.
        var privacyOverrideScreenshot = s.EffectivePrivacyMode && s.PrivacyModeDisableRemoteScreenshots;

        return new SessionCapabilitiesDto
        {
            // Play was the weakest of the gated flags even on its own
            // terms: it replaces what is playing rather than acting on it,
            // so the server deliberately does not refuse it for a private
            // item either. What arrives is a *new* item, and its privacy
            // is resolved from the settings this client now sends.
            CanPlay = s.AllowRemotePlay,
            // The four transport commands. All are refused server-side
            // while the current item is private, so the gate here only
            // ever hid the button a moment earlier.
            CanResumeOrPause = hasActivePlayback,
            CanSeek = hasActivePlayback,
            CanStop = hasActivePlayback,
            // The one inbound capability, and the only one that is not a
            // dispatch gate: it says this client *reports*, not that
            // something may be done to it. So it is a property of the
            // build and not of what is loaded, and gating it on playback
            // was a category error with teeth — the states this companion
            // most needs to report are `Stopped` and `Idle`, which are by
            // definition the ones where nothing is playing. The server
            // refuses a report from a session that declared it does not
            // report, correctly, and the refusal left it holding the last
            // state it had accepted: a position and a duration for an item
            // that had stopped. Frozen, not stale — and the freeze also
            // kept a private item's shape on the wire after the viewer
            // stopped it. There is no switch that turns reporting off, so
            // this is an unconditional yes.
            CanReportState = true,
            CanCaptureScreenshot = s.AllowRemoteScreenshot && hasActivePlayback && !privacyOverrideScreenshot,
            CanScreenshotAtPosition = s.AllowRemoteScreenshot && hasActivePlayback && !privacyOverrideScreenshot,
            MaxVolume = PlaybackCoordinator.MaxMpvVolume,
            // Volume is guarded server-side for a private item too, which
            // is stricter than it needs to be and is not ours to relax.
            CanSetVolume = s.AllowRemoteVolumeControl,
            CanSkipItems = s.AllowRemotePlay,
            // mpv supports both the `speed` and `fullscreen` properties, so
            // remote control is gated on the remote-play setting alone.
            CanChangePlaybackRate = s.AllowRemotePlay,
            CanChangeFullscreen = s.AllowRemotePlay,
            // mpv supports the full playlist contract (provide/reorder/jump),
            // gated on the same remote-play setting as the other remote
            // commands. Providing and reordering are not refused for a
            // private item server-side, and do not need to be: a private
            // entry reaches an observer as two opaque fields, so a queue
            // somebody can rearrange is a queue they still cannot read.
            // Jumping is refused, being a transport command wearing a
            // playlist's clothes.
            CanProvidePlaylist = s.AllowRemotePlay,
            CanReorderPlaylist = s.AllowRemotePlay,
            CanJumpToPlaylistItem = s.AllowRemotePlay,
            // Receiving a handoff is starting playback on somebody else's
            // say-so, so it rides on the remote-play setting as well as its
            // own switch: turning remote play off must not leave a back
            // door that starts a video here anyway. Deliberately not gated
            // on _hasActivePlayback — the usual reason to hand a video to
            // this device is that it is sitting idle. And no longer gated
            // on privacy: privacy crosses a handoff on the *item*, which
            // arrives already marked private, so a viewer in privacy mode
            // stays able to pull their own video over from their phone
            // instead of the feature quietly vanishing when they need it.
            CanReceiveHandoff = s.AllowSessionHandoff && s.AllowRemotePlay,
            // mpv switches a track in place - it costs a decoder reset and
            // nothing else - so this is a plain yes wherever remote control
            // is allowed at all. It needs something loaded to switch
            // within, which is what _hasActivePlayback says; a remote
            // reading false while nothing is playing is reading the truth,
            // and the flag is re-pushed the moment playback starts.
            CanSelectTracks = s.AllowRemotePlay && hasActivePlayback,
        };
    }

    /// <summary>
    ///   Build the settings this session declares — how it is configured,
    ///   as opposed to what it is able to do.
    ///
    ///   <para>
    ///     Three fields, and each is driven by a switch the companion
    ///     already had. The server owns what they mean: it resolves
    ///     privacy per item, ratchets it so it never comes off, withholds
    ///     a private item from every observer as two fields, and refuses
    ///     remote control of one. None of that happens until it is told,
    ///     and until this method existed it never was.
    ///   </para>
    ///   <para>
    ///     <b>The master switch sends <see cref="CompanionSettings.PrivacyMode"/>,
    ///     not <c>EffectivePrivacyMode</c>.</b> The effective value folds in
    ///     the restricted-content auto-trigger, and the two fields are not
    ///     interchangeable on the far side: the server's master switch
    ///     re-resolves the <em>whole queue</em> on its off→on transition,
    ///     while its restricted rule deliberately never reaches back into
    ///     the queue. Sending the effective value would make a restricted
    ///     episode starting retroactively privatise everything already
    ///     queued — the exact asymmetry the server rules against. So the
    ///     two travel on their own fields and the server applies each
    ///     rule with its own reach.
    ///   </para>
    ///   <para>
    ///     <c>BufferAheadSeconds</c> is not sent. mpv holds its own cache
    ///     and the companion has no configured forward target to report,
    ///     and silence there means "no opinion, use the default" rather
    ///     than zero.
    ///   </para>
    /// </summary>
    internal static SessionSettingsDto BuildCurrentSettings()
    {
        var s = SettingsProvider.Instance.Settings;

        return new SessionSettingsDto
        {
            PrivacyModeEnabled = s.PrivacyMode,
            AlwaysUsePrivacyModeForRestrictedContent = s.PrivacyModeForRestrictedContent,
            // One switch, two writers — and they take turns rather than
            // overlap, which is what makes sending this necessary rather
            // than merely tidy. While a media session is registered the
            // companion stands its own APIv3 scrobbler down entirely and
            // the server writes instead; without this field the viewer's
            // "do not record this" would hold only while the hub was
            // *down*, and silently stop meaning anything the moment it
            // came up. Which is the more common state, and the one they
            // are less likely to notice.
            DisablePlaybackEventSyncing = s.PrivacyModeDisablePlaybackEvents,
        };
    }

    /// <summary>
    ///   Build current capability flags from settings + playback state
    ///   and push them to the hub so the dashboard reacts immediately.
    ///
    ///   <para>
    ///     <b>Unchanged declarations are not sent</b>, exactly as
    ///     <see cref="UpdateSettingsOnHubAsync"/> declines to. That is
    ///     what lets every caller push freely — a setting saved, a state
    ///     reported, a file loaded — without any of them having to work
    ///     out first whether the answer actually moved. The alternative is
    ///     a list of the places that may push, and a list is the thing
    ///     that goes stale: a privacy toggle changed what this client
    ///     believed it could do and told nobody, because the settings
    ///     event was not on the list.
    ///   </para>
    /// </summary>
    /// <param name="force">
    ///   Send even when the declaration matches the last one accepted.
    ///   Used after a reconnect, where what the server holds is not known.
    /// </param>
    public async Task UpdateCapabilitiesOnHubAsync(bool force = false)
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected || _sessionId is null)
            return;

        var caps = BuildCurrentCapabilities(_hasActivePlayback);
        if (!force && caps == _lastSentCapabilities)
            return;

        try
        {
            await _connection.InvokeAsync("UpdateCapabilities", caps);
            _lastSentCapabilities = caps;
        }
        catch (Exception ex)
        {
            // Left unrecorded so the next recompute retries rather than
            // comparing against a declaration that never landed.
            _lastSentCapabilities = null;
            Logger.Debug(ex, "MediaSession: Failed to update capabilities");
        }
    }

    /// <summary>
    ///   Push the current settings to the hub, so a switch the viewer just
    ///   flipped reaches the server that enforces it.
    ///
    ///   <para>
    ///     Turning privacy on mid-session is the case this exists for, and
    ///     it is not merely cosmetic on the far side: the server's master
    ///     switch re-resolves the whole queue on its off→on transition, so
    ///     what is already queued is privatised too rather than only what
    ///     is added next.
    ///   </para>
    ///   <para>
    ///     <b>Unchanged declarations are not sent.</b> Settings are saved
    ///     far more often than they change in any way this cares about —
    ///     volume and fullscreen persist through the same file — and each
    ///     push costs a hub round trip and a state broadcast to every
    ///     observer. Comparing against what was last accepted is what
    ///     keeps "on change" meaning on change.
    ///   </para>
    /// </summary>
    /// <param name="force">
    ///   Send even when the declaration matches the last one accepted.
    ///   Used after a reconnect, where what the server holds is not known.
    /// </param>
    public async Task UpdateSettingsOnHubAsync(bool force = false)
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected || _sessionId is null)
            return;

        var settings = BuildCurrentSettings();
        if (!force && settings == _lastSentSettings)
            return;

        try
        {
            await _connection.InvokeAsync("UpdateSettings", settings);
            _lastSentSettings = settings;
            Logger.Debug(
                "MediaSession: Settings pushed (privacy={Privacy}, "
                + "restricted={Restricted}, noSync={NoSync})",
                settings.PrivacyModeEnabled,
                settings.AlwaysUsePrivacyModeForRestrictedContent,
                settings.DisablePlaybackEventSyncing);
        }
        catch (Exception ex)
        {
            // Left unrecorded deliberately, so the next change retries
            // rather than comparing against a declaration that never
            // landed. Warn rather than Debug: a privacy switch that did
            // not reach the server is the failure this whole path exists
            // to prevent, and it is otherwise silent.
            _lastSentSettings = null;
            Logger.Warn(ex, "MediaSession: Failed to update settings");
        }
    }

    /// <summary>
    ///   Settings were saved. Push whatever this session declares, if any
    ///   of it actually moved.
    ///
    ///   <para>
    ///     <b>Both declarations, not only the settings one.</b> Half the
    ///     capability flags are computed from settings — the four
    ///     <c>AllowRemote…</c> switches, the handoff switch, and the
    ///     screenshot pair, which privacy still gates — so a save that
    ///     pushed only <c>SessionSettings</c> left the server holding
    ///     capabilities the client had already stopped believing. Toggling
    ///     privacy was the sharp case: it changes what this client says it
    ///     can do and used to reach the server on neither event, since the
    ///     capability push hung off registration, the settings window,
    ///     reconnect and the loaded-file flag, and privacy is raised by
    ///     none of those.
    ///   </para>
    /// </summary>
    private void OnSettingsChanged(CompanionSettings settings)
    {
        _ = UpdateSettingsOnHubAsync();
        _ = UpdateCapabilitiesOnHubAsync();
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
        SettingsProvider.Instance.SettingsChanged -= OnSettingsChanged;
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
            SetSessionId(null);
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

        [JsonProperty("Settings")]
        public SessionSettingsDto Settings { get; init; } = new();
    }

    /// <summary>
    ///   How this session is configured, mirroring the plugin's
    ///   <c>SessionSettings</c>. Sent on the registration payload beside
    ///   the capabilities, and replaced wholesale afterwards through
    ///   <c>UpdateSettings</c>.
    ///
    ///   <para>
    ///     A record rather than a class, unlike its neighbours, because
    ///     the only question ever asked of two of these is whether they
    ///     differ — see <see cref="UpdateSettingsOnHubAsync"/>, which
    ///     declines to push an unchanged declaration.
    ///   </para>
    /// </summary>
    internal sealed record SessionSettingsDto
    {
        [JsonProperty("PrivacyModeEnabled")]
        public bool PrivacyModeEnabled { get; init; }

        [JsonProperty("AlwaysUsePrivacyModeForRestrictedContent")]
        public bool AlwaysUsePrivacyModeForRestrictedContent { get; init; }

        [JsonProperty("DisablePlaybackEventSyncing")]
        public bool DisablePlaybackEventSyncing { get; init; }
    }

    /// <summary>
    ///   What this session is able to do, mirroring the plugin's
    ///   <c>SessionCapabilities</c>.
    ///
    ///   <para>
    ///     A record, like its settings neighbour and for the same reason:
    ///     the only question ever asked of two of these is whether they
    ///     differ — see <see cref="UpdateCapabilitiesOnHubAsync"/>, which
    ///     declines to push an unchanged declaration.
    ///   </para>
    /// </summary>
    internal sealed record SessionCapabilitiesDto
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

        [JsonProperty("CanSkipItems")]
        public bool CanSkipItems { get; init; } = true;

        [JsonProperty("CanChangePlaybackRate")]
        public bool CanChangePlaybackRate { get; init; } = false;

        [JsonProperty("CanChangeFullscreen")]
        public bool CanChangeFullscreen { get; init; } = false;

        [JsonProperty("CanProvidePlaylist")]
        public bool CanProvidePlaylist { get; init; } = false;

        [JsonProperty("CanReorderPlaylist")]
        public bool CanReorderPlaylist { get; init; } = false;

        [JsonProperty("CanJumpToPlaylistItem")]
        public bool CanJumpToPlaylistItem { get; init; } = false;

        [JsonProperty("CanReceiveHandoff")]
        public bool CanReceiveHandoff { get; init; } = false;

        [JsonProperty("CanSelectTracks")]
        public bool CanSelectTracks { get; init; } = false;
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
    /// The currently playing media item, or null if none.
    /// </summary>
    [JsonProperty("CurrentItem")]
    public MediaItemInfoDto? CurrentItem { get; init; }

    /// <summary>
    /// Next item in the play queue, or null if none.
    /// </summary>
    [JsonProperty("NextItem")]
    public MediaItemInfoDto? NextItem { get; init; }

    /// <summary>
    /// Previous item in the play queue, or null if none.
    /// </summary>
    [JsonProperty("PreviousItem")]
    public MediaItemInfoDto? PreviousItem { get; init; }

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
    /// The current playback speed multiplier (e.g. 1.0, 4.0), or null
    /// when unknown.
    /// </summary>
    [JsonProperty("PlaybackSpeed")]
    public double? PlaybackSpeed { get; init; }

    /// <summary>
    /// Whether the player window is currently fullscreen, or null when
    /// unknown.
    /// </summary>
    [JsonProperty("IsFullscreen")]
    public bool? IsFullscreen { get; init; }

    /// <summary>
    /// The tracks this session is playing with, or null to say nothing.
    ///
    /// The only thing that writes the selection the plugin stores, and so
    /// the only thing a handoff carries onwards. <c>SetTracks</c> goes the
    /// other way and records nothing, which is why a switch has to be
    /// reported here after it lands rather than assumed when it is asked
    /// for.
    /// </summary>
    [JsonProperty("Tracks")]
    public PlaybackTrackSelectionDto? Tracks { get; init; }
}

/// <summary>
/// Lean media item info sent in state updates, mirroring the server's
/// PlaybackStateUpdateMediaItemInfo.
/// </summary>
public sealed class MediaItemInfoDto
{
    /// <summary>
    /// Human-readable title, or null if unknown.
    /// </summary>
    [JsonProperty("Title")]
    public string? Title { get; init; }

    /// <summary>
    /// Media type hint: "video", "audio", or "unknown".
    /// </summary>
    [JsonProperty("MediaType")]
    public string? MediaType { get; init; }

    /// <summary>
    /// Shoko video ID, if the item is managed by Shoko.
    /// </summary>
    [JsonProperty("VideoId")]
    public int? VideoId { get; init; }

    /// <summary>
    /// Stream URL for the item, if known. Used for items that are
    /// not Shoko-managed (no <see cref="VideoId"/>).
    /// </summary>
    [JsonProperty("StreamUrl")]
    public string? StreamUrl { get; init; }
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
