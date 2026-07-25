using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using System.Web;
using System.Threading.Tasks;
using Avalonia.Threading;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Discord;
using Shoko.Companion.Launch;
using Shoko.Companion.Mpv;
using Shoko.Companion.Notifications;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;
using Shoko.Companion.Windows;

namespace Shoko.Companion.Playback;

/// <summary>
/// Coordinates playback, scrobbling, and Discord presence for Shoko media items.
/// Implements the full playback lifecycle: URL resolution, mpv launch, property observation,
/// periodic scrobbling, and cleanup on stop or disconnect.
/// </summary>
public partial class PlaybackCoordinator : IPlaybackCoordinator, IAsyncDisposable
{
    private static readonly Regex FileIdPattern = FileIdRegex();

    // ── Mpv property names (used in both observation and event dispatch) ────
    private const string MpvPropTimePos = "time-pos";
    private const string MpvPropPause = "pause";
    private const string MpvPropPath = "path";
    private const string MpvPropEofReached = "eof-reached";
    private const string MpvPropIdleActive = "idle-active";
    private const string MpvPropAid = "aid";
    private const string MpvPropSid = "sid";
    private const string MpvPropVolume = "volume";

    // ── Thresholds ──────────────────────────────────────────────────────
    private const double MinResumeSeconds = 5;
    private const int FileLoadedDelayMs = 300;

    private readonly IShokoApiClient _apiClient;
    private readonly IMpvController _mpv;
    private readonly INotificationService _notifications;
    private readonly IDiscordPresenceService _discord;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly PlaybackSessionManager _sessionManager;

    private PlaybackState _state = PlaybackState.Idle;
    private double _duration;
    private int? _sessionFileId;
    private List<PlaylistItemDto>? _playlistItems;
    private Dictionary<int, StreamMetadata> _streamMetadata = [];
    private readonly Dictionary<int, MediaInfoDto> _mediaInfo = [];
    private Timer? _idleTimer;
    private DateTime? _idleStartTime;
    private int _pendingPlaylistEntries;

    // Stream selection carryover (languages last deliberately chosen by the user)
    private string? _carryoverAudioLang;
    private string? _carryoverSubLang;
    // True while a file is loading; suppresses treating default/restore track
    // selections as deliberate user changes.
    private bool _streamInitPhase;

    // False until the saved volume has been restored on the first file of a session.
    // Prevents the initial mpv property-change for volume (which fires at startup
    // with mpv's default value) from overwriting the persisted volume in settings.
    private bool _volumeRestored;

    /// <summary>
    /// Gets the current playback state.
    /// </summary>
    public PlaybackState CurrentState => _state;

    /// <inheritdoc/>
    public int? CurrentFileId => _sessionManager.CurrentFileId;

    /// <inheritdoc/>
    public double CurrentPositionSeconds => _sessionManager.CurrentPositionMs / 1000.0;

    /// <inheritdoc/>
    public double? DurationSeconds => _duration > 0 ? _duration / 1000.0 : null;

    /// <inheritdoc/>
    public string? CurrentTitle
    {
        get
        {
            var fileId = _sessionManager.CurrentFileId;
            if (fileId.HasValue && _streamMetadata.TryGetValue(fileId.Value, out var meta))
                return meta.EpisodeName ?? meta.AnimeName;

            return null;
        }
    }

    /// <inheritdoc/>
    public string? CurrentStreamUrl
    {
        get
        {
            var fileId = _sessionManager.CurrentFileId;
            if (fileId.HasValue)
                return _apiClient.BuildStreamUrl(fileId.Value);

            return null;
        }
    }

    /// <summary>
    /// Raised when the playback state changes.
    /// </summary>
    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    /// <summary>
    ///   Raised periodically (≈ every 10 s) during playback with the
    ///   current position so the media session hub stays in sync.
    /// </summary>
    public event EventHandler<TimeSpan>? PositionTick;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCoordinator"/> class.
    /// Creates all internal services (API client, mpv controller, notifications, Discord)
    /// and wires mpv event handlers.
    /// </summary>
    public PlaybackCoordinator()
    {
        _apiClient = new ShokoApiClient(new HttpClient());
        _mpv = new MpvIpcClient();
        _notifications = PlatformNotificationService.Instance;
        _discord = new DiscordPresenceService();
        _sessionManager = new PlaybackSessionManager();

        WireMpvEvents();
        WireSessionManagerEvents();

        // Initialize Discord early so idle presence can show on startup
        EnsureDiscordInitialized();
        ResetDiscordPresence();
    }

    private void WireSessionManagerEvents()
    {
        _sessionManager.ScrobbleRequested += OnSessionScrobbleRequested;
        _sessionManager.DiscordPresenceChanged += OnSessionDiscordPresenceChanged;
        _sessionManager.PositionTick += (_, pos) => PositionTick?.Invoke(this, pos);
    }

    private void WireMpvEvents()
    {
        _mpv.PropertyChanged += (s, e) =>
        {
            try { OnMpvPropertyChanged(s, e); }
            catch (Exception ex) { Logger.Error(ex, "Unhandled mpv property change"); }
        };
        _mpv.MpvEvent += async (s, e) =>
        {
            try { await OnMpvEvent(s, e); }
            catch (Exception ex) { Logger.Error(ex, "Unhandled mpv event"); }
        };
        _mpv.Disconnected += async (s, e) =>
        {
            try { await OnMpvDisconnected(s, e); }
            catch (Exception ex) { Logger.Error(ex, "Unhandled mpv disconnect"); }
        };
    }

    /// <summary>
    /// Play a shoko:// URL (handles m3u8 → JSON resolution, mpv launch, scrobble, etc).
    /// </summary>
    public async Task PlayAsync(string shokoUrl, TimeSpan? startPosition = null, bool? append = null)
    {
        if (_state is PlaybackState.Playing or PlaybackState.Paused)
        {
            var action = append.HasValue
                ? (append.Value ? OnNewUrlBehavior.Append : OnNewUrlBehavior.Replace)
                : SettingsProvider.Instance.Settings.OnNewUrlAction;
            switch (action)
            {
                case OnNewUrlBehavior.Ignore:
                    _notifications.Show("Shoko Companion",
                        "Already playing.",
                        NotificationSeverity.Info);
                    Logger.Info("Playback rejected — already playing and OnNewUrlAction is Ignore");
                    return;

                case OnNewUrlBehavior.Append:
                    await AppendToPlaylistAsync(shokoUrl);
                    return;

                case OnNewUrlBehavior.Replace:
                    await StopAsync();
                    break;
            }
        }

        // Clear idle presence when starting playback
        StopIdleTimer();

        SetState(PlaybackState.Loading);

        try
        {
            // Parse URL
            var parsed = ShokoUrlParser.Parse(shokoUrl);
            if (parsed is null)
            {
                _notifications.Show("Playback Error", $"Invalid URL: {shokoUrl}", NotificationSeverity.Error);
                SetState(PlaybackState.Error, $"Invalid URL: {shokoUrl}");
                return;
            }

            if (parsed is not { IsPlayAction: true })
            {
                _notifications.Show("Playback Error", $"Unsupported action for playback: {parsed.Action}", NotificationSeverity.Error);
                SetState(PlaybackState.Error, $"Unsupported action: {parsed.Action}");
                return;
            }

            // Resolve the best base URL via RouteResolver (supports multi-route fallback)
            var baseUrl = await RouteResolver.ResolveBestBaseUrlAsync(parsed.ServerBaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _notifications.Show("Playback Error", "Failed to resolve base URL. Set one in settings.", NotificationSeverity.Error);
                SetState(PlaybackState.Error, "Failed to resolve base URL");
                return;
            }

            var apiKey = await ResolveCredentialsAsync(baseUrl);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _notifications.Show("Playback Error", "No API key configured. Set one in settings.", NotificationSeverity.Error);
                SetState(PlaybackState.Error, "No API key available");
                return;
            }

            _apiClient.SetApiKey(apiKey);
            _apiClient.SetBaseUrl(baseUrl);

            // Fetch playlist metadata (with media info for stream-selection mapping)
            var jsonUrl = $"{baseUrl}/api/v3/Playlist/Generate?playlist={parsed.PlaylistId}&include=MediaInfo&apikey={apiKey}";
            _playlistItems = await _apiClient.FetchPlaylistJsonAsync(jsonUrl);

            // If null due to 401, invalidate stored key, re-prompt, and retry once
            if (_playlistItems is null && _apiClient.LastResponseWasUnauthorized == true && baseUrl is not null)
            {
                var badRouteKey = RouteResolver.ExtractRouteKey(baseUrl);
                if (badRouteKey is not null)
                {
                    var badConn = SettingsProvider.Instance.Settings.GetConnectionByRouteKey(badRouteKey);
                    if (badConn is not null)
                    {
                        Logger.Info("Clearing stored API key for '{Name}' (401 on playlist fetch)", badConn.Name);
                        badConn.ApiKey = null;
                        SettingsProvider.Instance.Save();
                    }
                }

                var newKey = await ResolveCredentialsAsync(baseUrl);
                if (newKey is not null)
                {
                    _apiClient.SetApiKey(newKey);
                    apiKey = newKey;
                    _playlistItems = await _apiClient.FetchPlaylistJsonAsync(jsonUrl);
                }
            }

            if (_playlistItems is null || _playlistItems.Count == 0)
            {
                _notifications.Show("Playback Error", "No files returned from playlist", NotificationSeverity.Error);
                SetState(PlaybackState.Error, "Empty playlist");
                return;
            }

            CacheMediaInfo(_playlistItems);
            _pendingPlaylistEntries = _playlistItems.Count;

            // Read metadata from first item
            var firstItem = _playlistItems[0];
            var firstFile = firstItem.Parts?.FirstOrDefault();
            if (firstFile is null)
            {
                _notifications.Show("Playback Error", "First playlist item has no files", NotificationSeverity.Error);
                SetState(PlaybackState.Error, "No files in playlist");
                return;
            }

            // Use Shoko API duration (TimeSpan) as the canonical source; mpv's duration
            // (seconds via observed property) is used as a fallback for subsequent files.
            _duration = firstFile.Duration.TotalMilliseconds;

            var m3u8Url = $"{baseUrl}/api/v3/Playlist/Generate.m3u8?playlist={parsed.PlaylistId}&apikey={apiKey}";
            _streamMetadata = await ParseM3u8Async(m3u8Url);
            EnrichStreamMetadataFromPlaylist(_playlistItems, _streamMetadata);
            _streamInitPhase = true;

            // Reset volume-restored guard so the initial mpv volume property-change
            // doesn't overwrite the persisted value before we restore it.
            _volumeRestored = false;

            // Pre-fetch first file's user data for resume
            var userData = await _apiClient.FetchFileUserDataAsync(firstFile.ID);
            _sessionFileId = firstFile.ID;
            if (startPosition.HasValue && _streamMetadata.TryGetValue(firstFile.ID, out var metadata))
                _streamMetadata[firstFile.ID] = metadata with { StartPosition = startPosition };

            // Find or use configured mpv path
            var mpvPath = SettingsProvider.Instance.Settings.MpvPath;
            if (string.IsNullOrWhiteSpace(mpvPath))
            {
                mpvPath = await MpvProcess.FindMpvAsync();
                if (mpvPath is null)
                {
                    mpvPath = await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var dialog = new MpvNotFoundDialog();
                        if (TryGetParentWindow() is { } parent)
                        {
                            await dialog.ShowDialog(parent);
                        }
                        else
                        {
                            var tcs = new TaskCompletionSource<string?>();
                            dialog.Closed += (_, _) => tcs.TrySetResult(dialog.MpvPath);
                            dialog.Show();
                            return await tcs.Task;
                        }
                        return dialog.MpvPath;
                    });

                    if (string.IsNullOrWhiteSpace(mpvPath))
                    {
                        Logger.Info("User cancelled mpv setup — aborting playback");
                        SetState(PlaybackState.Error, "mpv not found");
                        return;
                    }
                }
                SettingsProvider.Instance.Settings.MpvPath = mpvPath;
                SettingsProvider.Instance.Save();
                Logger.Info("Discovered and saved mpv path: {Path}", mpvPath);
            }

            // Launch mpv and connect (starts hidden with --vo=null --pause)
            var ipcPath = MpvProcess.GetDefaultIpcPath();
            var connected = await _mpv.LaunchAndConnectAsync(mpvPath, ipcPath);
            if (!connected)
            {
                _notifications.Show("mpv Launch Failed",
                    "Could not launch mpv or connect to its IPC socket.",
                    NotificationSeverity.Error);
                SetState(PlaybackState.Error, "mpv launch failed");
                return;
            }

            // Register property observers BEFORE loading the file
            // so we don't miss the initial "path" change event
            await _mpv.ObservePropertyAsync(1, MpvPropTimePos);
            await _mpv.ObservePropertyAsync(2, MpvPropPause);
            await _mpv.ObservePropertyAsync(3, MpvPropPath);
            await _mpv.ObservePropertyAsync(4, MpvPropEofReached);
            await _mpv.ObservePropertyAsync(5, MpvPropIdleActive);
            await _mpv.ObservePropertyAsync(6, MpvPropAid);
            await _mpv.ObservePropertyAsync(7, MpvPropSid);
            await _mpv.ObservePropertyAsync(8, MpvPropVolume);

            // Load the m3u8 URL into mpv
            await _mpv.LoadFileAsync(m3u8Url);

            // Apply resume position once the file loads (handled in OnMpvEvent file-loaded)

            // Discord presence will be set by the session manager on first event
            EnsureDiscordInitialized();

            SetState(PlaybackState.Loading);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Playback startup failed");
            _notifications.Show("Playback Error", ex.Message, NotificationSeverity.Error);
            SetState(PlaybackState.Error, ex.Message);
        }
    }

    /// <summary>
    /// Append a shoko:// URL to the current mpv playlist without interrupting playback.
    /// </summary>
    private async Task AppendToPlaylistAsync(string shokoUrl)
    {
        var previousState = _state;
        SetState(PlaybackState.Loading);

        try
        {
            var parsed = ShokoUrlParser.Parse(shokoUrl);
            if (parsed is not { IsPlayAction: true })
            {
                _notifications.Show("Playback Error", "Unsupported action for playback", NotificationSeverity.Error);
                SetState(previousState);
                return;
            }

            var baseUrl = await RouteResolver.ResolveBestBaseUrlAsync(parsed.ServerBaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                SetState(previousState);
                return;
            }

            var apiKey = await ResolveCredentialsAsync(baseUrl);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SetState(previousState);
                return;
            }

            _apiClient.SetApiKey(apiKey);
            _apiClient.SetBaseUrl(baseUrl);

            // Fetch playlist metadata for scrobbling
            var jsonUrl = $"{baseUrl}/api/v3/Playlist/Generate?playlist={parsed.PlaylistId}&include=MediaInfo&apikey={apiKey}";
            var newItems = await _apiClient.FetchPlaylistJsonAsync(jsonUrl);

            // If null due to 401, invalidate stored key, re-prompt, and retry once
            if (newItems is null && _apiClient.LastResponseWasUnauthorized == true && baseUrl is not null)
            {
                var badRouteKey = RouteResolver.ExtractRouteKey(baseUrl);
                if (badRouteKey is not null)
                {
                    var badConn = SettingsProvider.Instance.Settings.GetConnectionByRouteKey(badRouteKey);
                    if (badConn is not null)
                    {
                        Logger.Info("Clearing stored API key for '{Name}' (401 on playlist fetch)", badConn.Name);
                        badConn.ApiKey = null;
                        SettingsProvider.Instance.Save();
                    }
                }

                var newKey = await ResolveCredentialsAsync(baseUrl);
                if (newKey is not null)
                {
                    _apiClient.SetApiKey(newKey);
                    apiKey = newKey;
                    newItems = await _apiClient.FetchPlaylistJsonAsync(jsonUrl);
                }
            }

            if (newItems is null || newItems.Count == 0)
            {
                _notifications.Show("Playback Error", "No files returned from playlist", NotificationSeverity.Error);
                SetState(previousState);
                return;
            }

            // Extend our item/metadata tracking
            _playlistItems ??= [];
            _playlistItems.AddRange(newItems);
            CacheMediaInfo(newItems);

            // Parse m3u8 for stream metadata
            var m3u8Url = $"{baseUrl}/api/v3/Playlist/Generate.m3u8?playlist={parsed.PlaylistId}&apikey={apiKey}";
            var newMetadata = await ParseM3u8Async(m3u8Url);
            foreach (var kvp in newMetadata)
                _streamMetadata[kvp.Key] = kvp.Value;

            EnrichStreamMetadataFromPlaylist(newItems, _streamMetadata);

            // Append to mpv playlist
            await _mpv.AppendFileAsync(m3u8Url);
            _pendingPlaylistEntries++;

            Logger.Info("Appended to playlist: {Count} items (pending entries: {Pending})",
                newItems.Count, _pendingPlaylistEntries);

            _notifications.Show("Shoko Companion", "Added to playlist", NotificationSeverity.Info);
            SetState(previousState);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to append to playlist");
            _notifications.Show("Playback Error", ex.Message, NotificationSeverity.Error);
            SetState(previousState);
        }
    }

    /// <summary>
    /// Stop playback and kill mpv if we launched it.
    /// </summary>
    public async Task StopAsync()
    {
        if (_state is PlaybackState.Idle or PlaybackState.Stopped)
            return;

        // Send final stop scrobble via session manager
        var endPosition = _sessionManager.CurrentPositionMs;
        _sessionManager.EndSession(endPosition);

        _sessionFileId = null;
        await _mpv.StopAsync();

        ResetDiscordPresence();
        _duration = 0;
        _pendingPlaylistEntries = 0;
        _mediaInfo.Clear();
        _streamInitPhase = false;

        SetState(PlaybackState.Stopped);
    }

    /// <summary>
    /// Pause mpv.
    /// </summary>
    public async Task PauseAsync()
    {
        if (_state != PlaybackState.Playing) return;
        await _mpv.SetPropertyAsync(MpvPropPause, true);
    }

    /// <summary>
    /// Resume mpv.
    /// </summary>
    public async Task ResumeAsync()
    {
        if (_state != PlaybackState.Paused) return;
        await _mpv.SetPropertyAsync(MpvPropPause, false);
    }

    /// <inheritdoc/>
    public async Task SeekAsync(TimeSpan position)
    {
        if (_state != PlaybackState.Playing && _state != PlaybackState.Paused)
            return;

        Logger.Info("Seeking to {Position}", position);
        await _mpv.SetPropertyAsync("time-pos", position.TotalSeconds);
        _sessionManager.OnSeek(position.TotalMilliseconds);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        if (_state is not (PlaybackState.Playing or PlaybackState.Paused))
            return null;

        var tempPath = Path.GetTempFileName() + ".png";
        try
        {
            await _mpv.SendCommandAsync("screenshot-to-file", [tempPath]);

            if (!File.Exists(tempPath))
                return null;

            return await File.ReadAllBytesAsync(tempPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Screenshot capture failed");
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private void OnMpvPropertyChanged(object? sender, MpvPropertyChangeEventArgs args)
    {
        switch (args.Name)
        {
            case MpvPropTimePos:
                if (args.Data is double pos)
                    _sessionManager.OnPositionChanged(pos * 1000);
                else if (args.Data is long lpos)
                    _sessionManager.OnPositionChanged(lpos * 1000);
                break;

            case MpvPropPause:
                var isPaused = args.Data is true;
                if (isPaused && _pendingPlaylistEntries == 1
                    && (_sessionManager.HasReachedEof
                        || (_duration > 0 && _sessionManager.CurrentPositionMs > 0
                            && (_duration - _sessionManager.CurrentPositionMs) < 300)))
                {
                    Logger.Info("End-of-file pause detected (pos={Pos:F0}, dur={Dur:F0}) — stopping",
                        _sessionManager.CurrentPositionMs, _duration);
                    _ = StopAsync();
                }
                else
                {
                    _sessionManager.OnPauseChanged(isPaused);
                    if (_sessionManager.HasActiveSession)
                        SetState(isPaused ? PlaybackState.Paused : PlaybackState.Playing);
                }
                break;

            case MpvPropPath:
                if (args.Data is string path)
                    HandlePathChanged(path);
                break;

            case MpvPropEofReached:
                if (args.Data is true)
                    _sessionManager.OnEofReached();
                break;

            case MpvPropIdleActive:
                if (args.Data is true && _state is PlaybackState.Playing or PlaybackState.Paused)
                {
                    Logger.Info("mpv is now idle — playback finished, closing player");
                    _ = StopAsync();
                }
                break;

            case MpvPropAid:
                HandleTrackChanged(StreamKind.Audio, args.Data);
                break;

            case MpvPropSid:
                HandleTrackChanged(StreamKind.Subtitle, args.Data);
                break;

            case MpvPropVolume:
                if (args.Data is long vl)
                    PersistVolume((int)vl);
                else if (args.Data is int vi)
                    PersistVolume(vi);
                else if (args.Data is double vd)
                    PersistVolume((int)vd);
                else
                    Logger.Debug("Volume property-change with unexpected type: {Type} value={Value}",
                        args.Data?.GetType().Name, args.Data);
                break;
        }
    }

    private void HandlePathChanged(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var match = FileIdPattern.Match(path);
        if (!match.Success) return;

        var fileId = int.Parse(match.Groups[1].Value);

        // Entering a new file — suppress treating default track selections as user changes
        _streamInitPhase = true;

        // Update duration from API data for this file
        var fileDuration = _playlistItems?
            .SelectMany(i => i.Parts)
            .FirstOrDefault(p => p.ID == fileId)
            ?.Duration.TotalMilliseconds;
        if (fileDuration is > 0)
            _duration = fileDuration.Value;

        if (_streamMetadata.TryGetValue(fileId, out var meta))
        {
            var isRestricted = meta.IsRestricted;

            Logger.Info("Now playing file ID {FileId}: {Series} - Episode {EpNo} - {Episode}",
                fileId, meta.AnimeName, meta.EpisodeNumber, meta.EpisodeName);

            if (_sessionManager.HasActiveSession)
            {
                // Track switch mid-playlist — update metadata without ending session
                var epName = meta.EpisodeName;
                if (epName is null && _playlistItems?.FirstOrDefault()?.Episode is { } ep)
                    epName = ep.Title;

                var nextEpIds = new PlaylistEpisodeIDsDto
                {
                    TmdbShow = meta.TmdbShow,
                    TmdbMovie = meta.TmdbMovie,
                    TvdbShow = meta.TvdbShow,
                    ImdbMovie = meta.ImdbMovie,
                };
                _sessionManager.OnNextFile(fileId, 0, _duration, isRestricted,
                    meta.AnimeName, epName, meta.EpisodeNumber, 0, meta.EpisodeCount,
                    meta.PosterUrl, meta.AnimeId, nextEpIds);
            }
        }
        else
        {
            // Fall back to stream URL query params
            var isRestricted = string.Equals(ExtractQueryParam(path, "restricted"), "true", StringComparison.OrdinalIgnoreCase);
            var seriesTitle = ExtractQueryParam(path, "animeName");
            var episodeTitle = ExtractQueryParam(path, "episodeName");
            int.TryParse(ExtractQueryParam(path, "epNo"), out var epNo);
            int.TryParse(ExtractQueryParam(path, "epCount"), out var epCount);
            int.TryParse(ExtractQueryParam(path, "animeId"), out var animeId);
            var posterUrl = isRestricted ? null : ExtractQueryParam(path, "posterUrl");

            Logger.Info("Now playing file ID {FileId}: {Series} - {Episode}",
                fileId, seriesTitle, episodeTitle);

            if (_sessionManager.HasActiveSession)
            {
                _sessionManager.OnNextFile(fileId, 0, _duration, isRestricted,
                    seriesTitle, episodeTitle, epNo, 0, epCount,
                    posterUrl, animeId);
            }
        }
    }

    private enum StreamKind { Audio, Subtitle }

    /// <summary>
    /// Cache the media info of each file in the playlist by file ID.
    /// </summary>
    private void CacheMediaInfo(List<PlaylistItemDto> items)
    {
        foreach (var item in items)
        {
            if (item.Parts is null) continue;
            foreach (var part in item.Parts)
            {
                if (part.MediaInfo is not null)
                    _mediaInfo[part.ID] = part.MediaInfo;
            }
        }
    }

    /// <summary>
    /// Handle an mpv audio (aid) or subtitle (sid) track change. Maps the per-type
    /// mpv track index to a Shoko container stream ID, stores it for persistence, and
    /// (for genuine user changes) records the language for cross-file carryover.
    /// </summary>
    private void HandleTrackChanged(StreamKind kind, object? data)
    {
        var mpvId = data switch
        {
            long l => (int?)l,
            int i => i,
            _ => null   // "no" / false / null → disabled
        };

        var fileId = _sessionManager.CurrentFileId;
        if (fileId is null || !_mediaInfo.TryGetValue(fileId.Value, out var mi))
            return;

        var streams = kind == StreamKind.Audio ? mi.Audio : mi.Subtitles;

        MediaStreamDto? stream = null;
        if (mpvId is { } id && id >= 1 && id <= streams.Count)
            stream = streams[id - 1];

        var shokoId = stream?.ID;

        if (kind == StreamKind.Audio)
            _sessionManager.SetAudioStream(shokoId);
        else
            _sessionManager.SetSubtitleStream(shokoId);

        // Only record the language as a carryover preference for deliberate user changes.
        if (!_streamInitPhase)
        {
            var lang = stream?.LanguageCode;
            if (kind == StreamKind.Audio)
                _carryoverAudioLang = lang;
            else
                _carryoverSubLang = lang;

            Logger.Debug("User selected {Kind} track: lang={Lang}, mpvId={MpvId}, shokoId={ShokoId}",
                kind, lang, mpvId, shokoId);
        }
    }

    /// <summary>
    /// Restore audio and subtitle selections for the given file using the saved user
    /// data and the in-session language carryover. Updates the session's stored selections.
    /// </summary>
    private async Task RestoreStreamsAsync(int fileId, VideoUserDataDto? ud)
    {
        if (!_mediaInfo.TryGetValue(fileId, out var mi))
            return;

        var aid = ResolveRestoreTrack(StreamKind.Audio, mi, ud?.LastAudioStreamIndex, _carryoverAudioLang);
        if (aid is { } a)
        {
            Logger.Info("Restoring audio track: mpv aid={Aid} (lang={Lang})", a, mi.Audio[a - 1].LanguageCode);
            await _mpv.SetPropertyAsync(MpvPropAid, a);
            _sessionManager.SetAudioStream(mi.Audio[a - 1].ID);
        }

        var sid = ResolveRestoreTrack(StreamKind.Subtitle, mi, ud?.LastSubtitleStreamIndex, _carryoverSubLang);
        if (sid is { } s)
        {
            Logger.Info("Restoring subtitle track: mpv sid={Sid} (lang={Lang})", s, mi.Subtitles[s - 1].LanguageCode);
            await _mpv.SetPropertyAsync(MpvPropSid, s);
            _sessionManager.SetSubtitleStream(mi.Subtitles[s - 1].ID);
        }
    }

    /// <summary>
    /// Determine the mpv per-type track index (1-based) to restore for a stream kind,
    /// preferring a carried-over language match, then the saved server stream ID.
    /// Returns null if nothing should be restored.
    /// </summary>
    private int? ResolveRestoreTrack(StreamKind kind, MediaInfoDto mi, int? savedStreamId, string? carryoverLang)
    {
        var streams = kind == StreamKind.Audio ? mi.Audio : mi.Subtitles;
        if (streams.Count == 0) return null;

        // 1. Language carryover from a previous file in this session
        if (carryoverLang is not null)
        {
            for (var i = 0; i < streams.Count; i++)
            {
                if (string.Equals(streams[i].LanguageCode, carryoverLang, StringComparison.OrdinalIgnoreCase))
                    return i + 1;
            }
        }

        // 2. Saved server stream ID (per-file persistence across restarts)
        if (savedStreamId is { } id)
        {
            for (var i = 0; i < streams.Count; i++)
            {
                if (streams[i].ID == id)
                    return i + 1;
            }
        }

        return null;
    }

    private async Task OnMpvEvent(object? sender, MpvEventArgs args)
    {
        switch (args.Event)
        {
            case "file-loaded":
                Logger.Info("File loaded in mpv");
                await Task.Delay(FileLoadedDelayMs);

                // Only apply fullscreen on the first file of a playlist. Once the user
                // has manually toggled it off, subsequent files should not grab the screen.
                var isFirstFile = _sessionFileId.HasValue;

                // First file: _sessionFileId is set. Subsequent files: the session is
                // already active and CurrentFileId was set by HandlePathChanged.
                var fileId = _sessionFileId ?? _sessionManager.CurrentFileId;
                if (fileId.HasValue)
                {
                    var ud = await _apiClient.FetchFileUserDataAsync(fileId.Value);

                    // Seek to position: explicit start position (e.g. Media Session)
                    // takes precedence over the server-side resume position.
                    double resumeMs = 0;
                    if (_streamMetadata.TryGetValue(fileId.Value, out var fileMeta)
                        && fileMeta.StartPosition is { TotalSeconds: > MinResumeSeconds })
                    {
                        resumeMs = fileMeta.StartPosition.Value.TotalMilliseconds;
                        Logger.Info("Seeking to start position: {Pos}", fileMeta.StartPosition.Value);
                        await _mpv.SetPropertyAsync(MpvPropTimePos, fileMeta.StartPosition.Value.TotalSeconds);
                    }
                    else
                    {
                        var resume = ud?.ProgressPosition;
                        if (resume is not null && resume.Value.TotalSeconds > MinResumeSeconds)
                        {
                            resumeMs = resume.Value.TotalMilliseconds;
                            Logger.Info("Seeking to resume position: {Pos}", resume.Value);
                            await _mpv.SetPropertyAsync(MpvPropTimePos, resume.Value.TotalSeconds);
                        }
                    }

                    // Start the session on the first file (subsequent files reuse the active session)
                    if (_sessionFileId.HasValue)
                    {
                        var isRestricted = _streamMetadata.TryGetValue(fileId.Value, out var meta) && meta.IsRestricted;
                        var seriesTitle = meta?.AnimeName;
                        var episodeTitle = meta?.EpisodeName;
                        var epNumber = meta?.EpisodeNumber ?? 0;
                        var epCount = meta?.EpisodeCount ?? 0;
                        var posterUrl = meta?.PosterUrl;
                        var animeId = meta?.AnimeId ?? 0;

                        if (episodeTitle is null && _playlistItems?.FirstOrDefault()?.Episode is { } ep)
                            episodeTitle = ep.Title;

                        var epIds = _playlistItems?.FirstOrDefault()?.Episode?.IDs;
                        _sessionManager.StartSession(
                            fileId.Value, resumeMs, _duration, isRestricted,
                            seriesTitle, episodeTitle, epNumber, 0, epCount,
                            posterUrl, animeId, epIds
                        );

                        // If mpv was launched paused, the initial pause=true
                        // observer event fired before StartSession. Transition
                        // to Paused now so the session doesn't hang in Loading.
                        if (SettingsProvider.Instance.Settings.MpvStartPaused)
                            SetState(PlaybackState.Paused);

                        _sessionFileId = null;
                    }

                    // Restore audio/subtitle selections
                    await RestoreStreamsAsync(fileId.Value, ud);
                }

                // Initial track selection / restore is done — subsequent changes are user-driven
                _streamInitPhase = false;

                // Configure display — only on first file so the user can toggle fullscreen
                // off for subsequent items without the companion grabbing it back.
                if (SettingsProvider.Instance.Settings.MpvFullScreen && isFirstFile)
                    await _mpv.SetPropertyAsync("fullscreen", true);

                // Set the mpv window title from the m3u8 EXTINF display title
                // so it shows the exact Shoko episode name, not whatever the
                // media file embeds. Falls back to AnimeName - EpisodeName.
                if (fileId.HasValue && _streamMetadata.TryGetValue(fileId.Value, out var winMeta))
                {
                    var winTitle = winMeta.M3u8Title
                        ?? (winMeta.AnimeName is not null && winMeta.EpisodeName is not null
                            ? $"{winMeta.AnimeName} - {winMeta.EpisodeName}"
                            : winMeta.AnimeName ?? winMeta.EpisodeName ?? $"<Video {fileId.Value}>");
                    await _mpv.SetPropertyAsync("force-media-title", winTitle);
                }

                var savedVolume = SettingsProvider.Instance.Settings.Volume;
                if (SettingsProvider.Instance.Settings.RestoreVolume && savedVolume.HasValue)
                    await _mpv.SetPropertyAsync(MpvPropVolume, savedVolume.Value);
                _volumeRestored = true;
                if (!SettingsProvider.Instance.Settings.MpvStartPaused)
                    await _mpv.SetPropertyAsync(MpvPropPause, false);
                break;

            case "seek":
                if (args.Data is Newtonsoft.Json.Linq.JObject seekData
                    && seekData.TryGetValue("time", out var timeToken)
                    && timeToken.Type == Newtonsoft.Json.Linq.JTokenType.Float)
                {
                    _sessionManager.OnSeek((double)timeToken * 1000);
                }
                break;

            case "end-file":
                var reason = (args.Data as Newtonsoft.Json.Linq.JObject)?["reason"]?.ToString();
                Logger.Info("mpv end-file: {Reason}", reason);
                if (reason == "eof")
                {
                    // Emit stop scrobble for the file that just ended before we
                    // transition to the next one. This ensures every file in a
                    // multi-item playlist gets its PlaybackEnd event.
                    _sessionManager.FinalizeCurrentFile();

                    if (_pendingPlaylistEntries > 0)
                    {
                        // Pause before the next file auto-loads so we can prep it
                        await _mpv.SetPropertyAsync(MpvPropPause, true);
                        _pendingPlaylistEntries--;
                        Logger.Debug("Playlist entry ended, {Pending} entries remaining", _pendingPlaylistEntries);
                    }
                    else
                    {
                        Logger.Debug("Last file ended — stopping playback");
                        await StopAsync();
                    }
                }
                break;
        }
    }

    private async Task OnMpvDisconnected(object? sender, EventArgs e)
    {
        Logger.Info("mpv disconnected — assuming user closed the player");
        if (_state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Loading)
        {
            await StopAsync();
        }
    }

    private void PersistVolume(int volume)
    {
        // Ignore volume property-changes that fire during mpv startup before
        // we've restored the saved volume. mpv reports its default (e.g. 100)
        // as soon as the property is observed, which would overwrite the value
        // we want to restore.
        if (!_volumeRestored)
        {
            Logger.Trace("Volume change ignored — volume not yet restored");
            return;
        }

        if (!SettingsProvider.Instance.Settings.RestoreVolume)
        {
            Logger.Trace("Volume change ignored — RestoreVolume is disabled");
            return;
        }

        Logger.Debug("Volume changed to {Volume}", Math.Clamp(volume, 0, 130));
        SettingsProvider.Instance.Settings.Volume = Math.Clamp(volume, 0, 130);
        SettingsProvider.Instance.Save();
    }

    private async void OnSessionScrobbleRequested(object? sender, ScrobbleRequestEventArgs e)
    {
        if (!SettingsProvider.Instance.Settings.PlaybackSyncingEnabled)
            return;

        try
        {
            var ok = await _apiClient.ScrobbleAsync(e.FileId, e.EventType, e.Position, e.IsWatched);
            if (ok)
                Logger.Debug("Scrobble {Event} for file {File}: pos={Pos}, watched={Watched}",
                    e.EventType, e.FileId, e.Position, e.IsWatched);
            else
                Logger.Warn("Scrobble {Event} for file {File} returned non-success",
                    e.EventType, e.FileId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Scrobble failed for file {File}", e.FileId);
        }

        // On stop, persist stream selections (and position) to user data
        if (e.PersistUserData && (e.AudioStreamId is not null || e.SubtitleStreamId is not null || e.VideoStreamId is not null))
        {
            try
            {
                var userData = await _apiClient.FetchFileUserDataAsync(e.FileId);
                await _apiClient.PutFileUserDataAsync(e.FileId, new VideoUserDataDto
                {
                    ProgressPosition = userData?.ProgressPosition,
                    LastWatchedAt = userData?.LastWatchedAt,
                    WatchedCount = userData?.WatchedCount ?? 0,
                    LastUpdatedAt = userData?.LastUpdatedAt ?? DateTime.Now,
                    LastVideoStreamIndex = e.VideoStreamId,
                    LastAudioStreamIndex = e.AudioStreamId,
                    LastSubtitleStreamIndex = e.SubtitleStreamId,
                });
                Logger.Debug("Persisted stream selections for file {File}: a={A}, s={S}, v={V}",
                    e.FileId, e.AudioStreamId, e.SubtitleStreamId, e.VideoStreamId);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to persist user data for file {File}", e.FileId);
            }
        }
    }

    private void OnSessionDiscordPresenceChanged(object? sender, DiscordPresenceData? presence)
    {
        if (!_discord.IsInitialized) return;

        if (presence is not null)
            _discord.SetPresence(presence);
        else
            ResetDiscordPresence();
    }

    private void ResetDiscordPresence()
    {
        StopIdleTimer();

        if (SettingsProvider.Instance.Settings.DiscordIdlePresence)
        {
            _discord.SetIdlePresence();
            StartIdleTimer();
        }
        else
        {
            _discord.ClearPresence();
        }
    }

    private void EnsureDiscordInitialized()
    {
        if (_discord.IsInitialized) return;
        var s = SettingsProvider.Instance.Settings;
        if (s.CanUseDiscord)
            _ = _discord.InitializeAsync(s.DiscordClientId);
    }

    /// <summary>
    /// Releases all resources. Stops playback, disposes the SignalR connection,
    /// shuts down Discord presence, and disposes the mpv controller.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        _discord.Shutdown();

        if (_mpv is IAsyncDisposable ad)
            await ad.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    private void StartIdleTimer()
    {
        StopIdleTimer();
        _idleStartTime = DateTime.UtcNow;
        // Fire at 10min to switch to "Idle", then at 60min to clear
        _idleTimer = new Timer(OnIdleTimer10m, null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
    }

    private void StopIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        _idleStartTime = null;
    }

    private void OnIdleTimer10m(object? state)
    {
        if (_idleStartTime is null) return;

        var elapsed = DateTime.UtcNow - _idleStartTime.Value;

        if (elapsed.TotalMinutes >= 60)
        {
            Logger.Info("Idle for 60m — clearing Discord presence");
            _discord.ClearPresence();
            StopIdleTimer();
        }
        else
        {
            Logger.Debug("Idle for 10m — switching to Idle state");
            _discord.SetPresence(new DiscordPresenceData(
                Details: "Shoko Companion",
                State: "Idle",
                LargeImageKey: null,
                LargeImageText: null,
                StartTimeStamp: null
            ));
            // Schedule next check at 60min
            _idleTimer?.Change(TimeSpan.FromMinutes(50), Timeout.InfiniteTimeSpan);
        }
    }

    private void SetState(PlaybackState newState, string? errorMessage = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, newState, errorMessage,
            newState is PlaybackState.Playing or PlaybackState.Paused ? "Playing" : null));
    }

    private static string? ExtractQueryParam(string url, string paramName)
    {
        var qsStart = url.IndexOf('?');
        if (qsStart < 0) return null;

        var query = HttpUtility.ParseQueryString(url[qsStart..]);
        return query[paramName];
    }

    /// <summary>
    /// Resolve an API key for the given server URL.
    /// Checks the connection's stored key first, then probes the server,
    /// and finally prompts the user for credentials if still not found.
    /// </summary>
    private async Task<string?> ResolveCredentialsAsync(string serverBaseUrl)
    {
        // First check if the connection already has a stored API key
        var routeKey = RouteResolver.ExtractRouteKey(serverBaseUrl);
        if (routeKey is not null)
        {
            var existingConn = SettingsProvider.Instance.Settings.GetConnectionByRouteKey(routeKey);
            if (existingConn?.ApiKey is { Length: > 0 })
            {
                Logger.Debug("Using stored API key for connection '{Name}'", existingConn.Name);
                return existingConn.ApiKey;
            }
        }

        // Probe the init endpoint to verify this is a valid Shoko server
        if (!await ProbeShokoInitAsync(serverBaseUrl))
        {
            _notifications.Show("Connection Error",
                $"Could not reach a Shoko server at {serverBaseUrl}.",
                NotificationSeverity.Error);
            return null;
        }

        var host = routeKey ?? serverBaseUrl;

        // Show credential prompt on the UI thread
        var apiKey = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new CredentialPromptDialog(serverBaseUrl, host);
            // Try to find an active window to parent the dialog
            if (TryGetParentWindow() is { } parent)
            {
                await dialog.ShowDialog(parent);
            }
            else
            {
                // No parent window — show modeless and wait via Closed event
                var tcs = new TaskCompletionSource<string?>();
                dialog.Closed += (_, _) => tcs.TrySetResult(dialog.ApiKey);
                dialog.Show();
                return await tcs.Task;
            }
            return dialog.ApiKey;
        });

        if (apiKey is not null && routeKey is not null)
        {
            // Persist the key on the matching connection
            var useHttps = serverBaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase);
            var conn = SettingsProvider.Instance.Settings.GetOrCreateConnectionByRouteKey(routeKey, useHttps);
            conn.ApiKey = apiKey;
            SettingsProvider.Instance.Save();
        }

        return apiKey;
    }

    /// <summary>
    /// Quick check: is there a valid Shoko server at this base URL?
    /// Probes /api/v3/Init/Version.
    /// </summary>
    private static async Task<bool> ProbeShokoInitAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/v3/Init/Version");
            if (!response.IsSuccessStatusCode)
                return false;

            var body = await response.Content.ReadAsStringAsync();
            return !string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith("{");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Shoko init probe failed for {BaseUrl}", baseUrl);
            return false;
        }
    }

    /// <summary>
    /// Download and parse the m3u8 playlist to extract stream-level metadata
    /// (animeName, epNo, epCount, posterUrl, animeId) from each stream URL's query params.
    /// These are not available from the JSON playlist endpoint.
    /// </summary>
    private async Task<Dictionary<int, StreamMetadata>> ParseM3u8Async(string m3u8Url)
    {
        var result = new Dictionary<int, StreamMetadata>();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var text = await http.GetStringAsync(m3u8Url);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("#EXTINF:")) continue;

                // Grab the display title: #EXTINF:-1,Display Title
                var extinfTitle = ParseExtinfTitle(lines[i]);

                // The stream URL is the next non-empty, non-comment line
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].StartsWith("#")) continue;
                    if (string.IsNullOrWhiteSpace(lines[j])) continue;

                    var match = FileIdPattern.Match(lines[j]);
                    if (match.Success)
                    {
                        var fileId = int.Parse(match.Groups[1].Value);
                        if (!result.ContainsKey(fileId))
                        {
                            result[fileId] = ParseStreamMetadata(lines[j], fileId) with
                            {
                                M3u8Title = extinfTitle
                            };
                        }
                    }
                    break;
                }
            }

            Logger.Debug("Parsed m3u8: extracted metadata for {Count} files", result.Count);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to parse m3u8 for metadata enrichment");
        }

        return result;
    }

    private static StreamMetadata ParseStreamMetadata(string url, int fileId)
    {
        var qsStart = url.IndexOf('?');
        if (qsStart < 0)
            return new StreamMetadata(fileId, null, null, 0, 0, null, 0, false);

        var query = System.Web.HttpUtility.ParseQueryString(url[qsStart..]);

        int.TryParse(query["epNo"], out var epNo);
        int.TryParse(query["epCount"], out var epCount);
        int.TryParse(query["animeId"], out var animeId);

        var isRestricted = string.Equals(query["restricted"], "true", StringComparison.OrdinalIgnoreCase);

        return new StreamMetadata(
            FileId: fileId,
            AnimeName: query["animeName"],
            EpisodeName: query["episodeName"],
            EpisodeNumber: epNo,
            EpisodeCount: epCount,
            PosterUrl: isRestricted ? null : query["posterUrl"],
            AnimeId: animeId,
            IsRestricted: isRestricted);
    }

    /// <summary>
    /// Extract the display title from an EXTINF line.
    /// Format: <c>#EXTINF:-1,Display Title</c>
    /// Returns the portion after the first comma, or null.
    /// </summary>
    private static string? ParseExtinfTitle(string line)
    {
        var comma = line.IndexOf(',');
        return comma >= 0 ? line[(comma + 1)..].Trim() : null;
    }

    /// <summary>
    /// Get the first visible window to use as a dialog parent, or null.
    /// </summary>
    private static Avalonia.Controls.Window? TryGetParentWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            return lifetime.Windows.FirstOrDefault(w => w.IsVisible);
        return null;
    }

    private static void EnrichStreamMetadataFromPlaylist(
        List<PlaylistItemDto> items, Dictionary<int, StreamMetadata> metadata)
    {
        foreach (var item in items)
        {
            var epIds = item.Episode.IDs;
            foreach (var part in item.Parts)
            {
                if (!metadata.TryGetValue(part.ID, out var existing)) continue;

                metadata[part.ID] = existing with
                {
                    TmdbShow = epIds.TmdbShow,
                    TmdbMovie = epIds.TmdbMovie,
                    TvdbShow = epIds.TvdbShow,
                    ImdbMovie = epIds.ImdbMovie
                };
            }
        }
    }

    [GeneratedRegex(@"/File/(\d+)/Stream", RegexOptions.IgnoreCase)]
    private static partial Regex FileIdRegex();

    private record StreamMetadata(
        int FileId,
        string? AnimeName,
        string? EpisodeName,
        int EpisodeNumber,
        int EpisodeCount,
        string? PosterUrl,
        int AnimeId,
        bool IsRestricted,
        TimeSpan? StartPosition = null,
        string? M3u8Title = null,
        int? TmdbShow = null,
        int? TmdbMovie = null,
        int? TvdbShow = null,
        string? ImdbMovie = null);
}
