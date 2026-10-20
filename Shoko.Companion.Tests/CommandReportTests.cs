using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json.Linq;
using Shoko.Companion.Configuration;
using Shoko.Companion.Discord;
using Shoko.Companion.Mpv;
using Shoko.Companion.Notifications;
using Shoko.Companion.Playback;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   Every command the companion carries out ends in a report of what
///   actually happened.
///
///   <para>
///     Since Kaname #335 the server applies no command itself: it sends
///     the command and changes nothing, and the session's own report is
///     what moves its state and what every observer sees. So a command
///     carried out without a report of its result leaves the server wrong
///     until something else happens to be reported, and a report of a
///     result that did not happen leaves it wrong until something corrects
///     it, which may be never.
///   </para>
///   <para>
///     The commands are driven through the coordinator with a mocked mpv
///     that answers a property set the way mpv does, by reporting the
///     property back, and a <see cref="FakeShokoServer"/> on loopback for
///     the <c>shoko://</c> URLs to resolve against. A report is read the
///     way every report call site in <c>App</c> reads it, from
///     <see cref="IPlaybackCoordinator.StateChanged"/>.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class CommandReportTests : IDisposable
{
    private const int VideoId = 29529;
    private const double DurationMs = 1_420_000; // 23:40

    private readonly bool _syncingEnabled;
    private readonly bool _discordEnabled;
    private readonly bool _privacyMode;
    private readonly bool _startPaused;
    private readonly bool _configuredRoutes;
    private readonly string? _lastUsedServerUrl;

    private readonly FakeShokoServer _server = new();
    private readonly Mock<IMpvController> _mpv = new();
    private readonly Mock<IShokoApiClient> _api = new();
    private readonly PlaybackSessionManager _manager = new();
    private readonly PlaybackCoordinator _coordinator;

    private readonly ConcurrentQueue<Report> _reports = new();
    private readonly ConcurrentQueue<PositionTickEventArgs> _ticks = new();

    private JArray _mpvPlaylist = [];
    private bool _mpvIdle = true;

    /// <summary>A state report as <c>App</c> builds it.</summary>
    private sealed record Report(PlaybackState State, double PositionSeconds, int? ItemVideoId, PlaybackTrackSelectionDto? Tracks);

    public CommandReportTests()
    {
        var s = SettingsProvider.Instance.Settings;
        _syncingEnabled = s.PlaybackSyncingEnabled;
        _discordEnabled = s.DiscordEnabled;
        _privacyMode = s.PrivacyMode;
        _startPaused = s.MpvStartPaused;
        _configuredRoutes = s.AlwaysUseConfiguredRoutes;
        _lastUsedServerUrl = s.LastUsedServerUrl;
        s.DiscordEnabled = false;
        s.MpvStartPaused = false;
        s.AlwaysUseConfiguredRoutes = false;
        // The setting the bug lives in: the server owns the viewing, so the
        // companion's reports are the only account of it.
        _manager.MediaSessionConnected = true;

        // An mpv that is running, sits idle until told to play, and answers
        // a property set by reporting the property back.
        _mpv.SetupGet(m => m.IsConnected).Returns(true);
        _mpv.Setup(m => m.GetPropertyAsync<JArray>("playlist", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _mpvPlaylist);
        _mpv.Setup(m => m.GetPropertyAsync<bool?>("idle-active", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _mpvIdle);
        _mpv.Setup(m => m.AppendListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((url, _) =>
            {
                var id = int.Parse(url.Split("playlist=f")[1].Split('&')[0]);
                _mpvPlaylist.Add(new JObject
                {
                    ["filename"] = _server.StreamUrl(id),
                    ["title"] = $"Episode {id}",
                    ["current"] = false,
                });
            })
            .Returns(Task.CompletedTask);
        _mpv.Setup(m => m.SetPropertyAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((name, value, _) =>
            {
                if (name is "pause" or "vid" or "aid" or "sid")
                    Property(name, value);
            })
            .Returns(Task.CompletedTask);

        _api.Setup(a => a.FetchPlaylistJsonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) => Items(VideoIdOf(url)));

        _coordinator = new PlaybackCoordinator(
            _api.Object, _mpv.Object, Mock.Of<INotificationService>(),
            Mock.Of<IDiscordPresenceService>(), _manager);

        _coordinator.StateChanged += (_, e) => _reports.Enqueue(new Report(
            e.NewState, _coordinator.CurrentPositionSeconds,
            _coordinator.CurrentItem?.VideoId, _coordinator.CurrentTracks));
        _coordinator.PositionTick += (_, e) => _ticks.Enqueue(e);
    }

    public void Dispose()
    {
        _manager.EndSession(_manager.CurrentPositionMs);
        _server.Dispose();
        var s = SettingsProvider.Instance.Settings;
        s.PlaybackSyncingEnabled = _syncingEnabled;
        s.DiscordEnabled = _discordEnabled;
        s.PrivacyMode = _privacyMode;
        s.MpvStartPaused = _startPaused;
        s.AlwaysUseConfiguredRoutes = _configuredRoutes;
        s.LastUsedServerUrl = _lastUsedServerUrl;
        s.RestrictedContentPlaying = false;
        GC.SuppressFinalize(this);
    }

    private void Property(string name, object? data)
        => _mpv.Raise(m => m.PropertyChanged += null, new MpvPropertyChangeEventArgs(1, name, data));

    private void Event(string name, object? data = null)
        => _mpv.Raise(m => m.MpvEvent += null, new MpvEventArgs(name, data));

    private static int VideoIdOf(string url)
        => int.Parse(url.Split("playlist=f")[1].Split('&')[0]);

    /// <summary>
    ///   What Shoko's <c>Playlist/Generate</c> returns for one video: one
    ///   file, with a video track, two audio tracks and one subtitle track.
    /// </summary>
    private static List<PlaylistItemDto> Items(int videoId)
        =>
        [
            new PlaylistItemDto
            {
                Episode = new PlaylistEpisodeDto { Title = "Episode", Number = 1 },
                Parts =
                [
                    new FileDto
                    {
                        ID = videoId,
                        Duration = TimeSpan.FromMilliseconds(DurationMs),
                        MediaInfo = new MediaInfoDto
                        {
                            Video = [new MediaStreamDto { ID = 1, LanguageCode = "jpn" }],
                            Audio =
                            [
                                new MediaStreamDto { ID = 2, LanguageCode = "jpn" },
                                new MediaStreamDto { ID = 3, LanguageCode = "eng" },
                            ],
                            Subtitles = [new MediaStreamDto { ID = 4, LanguageCode = "eng" }],
                        },
                    },
                ],
            },
        ];

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    ///   A queue added through the hub's <c>AddToPlaylist</c> while mpv
    ///   sits idle with nothing loaded.
    /// </summary>
    private async Task QueueInIdleMpvAsync()
    {
        // mpv is launched with --pause, and says so when observed.
        Property("pause", true);
        await _coordinator.AddToPlaylistAsync([new PlaylistAddition(_server.ShokoUrl(VideoId))], null);
        Assert.Equal(VideoId, Assert.Single(_coordinator.CurrentPlaylist).VideoId);
        Assert.False(_manager.HasActiveSession);
    }

    /// <summary>
    ///   What mpv does once told to play the queue's first entry: the
    ///   playlist moves onto it, <c>path</c> names it, and the file loads.
    ///   Waits out <c>file-loaded</c>, which is the coordinator's to finish.
    /// </summary>
    private async Task MpvLoadsAsync(int videoId)
    {
        _mpvIdle = false;
        foreach (var entry in _mpvPlaylist.Cast<JObject>())
            entry["current"] = entry.Value<string>("filename") == _server.StreamUrl(videoId);
        Property("playlist", _mpvPlaylist);
        Property("path", _server.StreamUrl(videoId));
        Event("file-loaded");
        await Until(() => _manager.HasActiveSession, "the session to start");
    }

    /// <summary>
    ///   That a session is running for <see cref="VideoId"/> and reports
    ///   like one: positions, a seek, and tracks, and that SetTracks reaches
    ///   mpv.
    /// </summary>
    private async Task AssertTheSessionReportsAsync()
    {
        Assert.Equal(VideoId, _manager.CurrentVideoId);

        // Positions: mpv's, on the tick the timer beats.
        Property("time-pos", 120.0);
        _manager.OnScrobbleTimer(null);
        await Until(() => _ticks.Any(t => !t.Seeked && Math.Abs(t.Position.TotalSeconds - 120.0) < 0.001), "a tick at 120 s");
        Assert.Equal(120.0, _coordinator.CurrentPositionSeconds, 3);

        // A seek, reported once it lands.
        Event("seek");
        Event("playback-restart");
        Property("time-pos", 600.0);
        await Until(() => _ticks.Any(t => t.Seeked), "the seek report");
        Assert.Equal(600.0, Assert.Single(_ticks, t => t.Seeked).Position.TotalSeconds, 3);

        // Tracks: what mpv says it is playing becomes the report's tracks.
        Property("vid", 1L);
        Property("aid", 1L);
        Property("sid", false);
        Assert.Equal(new PlaybackTrackSelectionDto { VideoOrdinal = 0, AudioOrdinal = 0, SubtitleIndex = -1 },
            _coordinator.CurrentTracks);

        // And SetTracks is not ignored: mpv is told, mpv answers, and the
        // answer is what is reported.
        await _coordinator.SetTracksAsync(new PlaybackTrackSelectionDto { AudioOrdinal = 1 });
        _mpv.Verify(m => m.SetPropertyAsync("aid", 2, It.IsAny<CancellationToken>()));
        Assert.Equal(1, _coordinator.CurrentTracks?.AudioOrdinal);
    }

    // ── 1. Playback started through the playlist commands has a session ──

    [Fact]
    public async Task AddToPlaylistThenResume_FromIdle_StartsASession()
    {
        await QueueInIdleMpvAsync();
        Assert.Equal(PlaybackState.Paused, _coordinator.CurrentState);

        await _coordinator.ResumeAsync();
        _mpv.Verify(m => m.SendCommandAsync("playlist-play-index", new object?[] { 0 }, It.IsAny<CancellationToken>()));

        await MpvLoadsAsync(VideoId);
        await Until(() => _coordinator.CurrentState is PlaybackState.Playing, "Playing");

        await AssertTheSessionReportsAsync();
    }

    [Fact]
    public async Task JumpToPlaylistItem_FromIdle_StartsASession()
    {
        await QueueInIdleMpvAsync();

        await _coordinator.JumpToPlaylistItemAsync(_server.StreamUrl(VideoId));
        _mpv.Verify(m => m.SendCommandAsync("playlist-play-index", new object?[] { 0 }, It.IsAny<CancellationToken>()));

        await MpvLoadsAsync(VideoId);
        await Until(() => _coordinator.CurrentState is PlaybackState.Playing, "Playing");

        await AssertTheSessionReportsAsync();
    }

    // ── 2. A jump reports what mpv does, not what it assumed ──

    [Fact]
    public async Task JumpToPlaylistItem_WithMpvStartPaused_NeverReportsPlaying()
    {
        SettingsProvider.Instance.Settings.MpvStartPaused = true;
        await QueueInIdleMpvAsync();
        _reports.Clear();

        await _coordinator.JumpToPlaylistItemAsync(_server.StreamUrl(VideoId));
        Assert.NotEqual(PlaybackState.Playing, _coordinator.CurrentState);

        await MpvLoadsAsync(VideoId);
        await Until(() => _coordinator.CurrentState is PlaybackState.Paused, "Paused");
        // Past the rest of file-loaded, which must leave mpv paused.
        await Task.Delay(200);

        Assert.Equal(PlaybackState.Paused, _coordinator.CurrentState);
        Assert.DoesNotContain(_reports, r => r.State is PlaybackState.Playing);
        Assert.Equal(PlaybackState.Paused, _reports.Last().State);
        _mpv.Verify(m => m.SetPropertyAsync("pause", false, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 3. The append path does not put back a stale state ──

    /// <summary>A session playing video 29529, as the coordinator sees it.</summary>
    private void StartPlaying(double positionSeconds = 60.0)
    {
        _mpvIdle = false;
        _manager.StartSession(VideoId, 0, DurationMs, false, "Series", "Episode", 1, 1, 12, null, 1);
        Property("time-pos", positionSeconds);
        Property("pause", false);
        Assert.Equal(PlaybackState.Playing, _coordinator.CurrentState);
    }

    [Fact]
    public async Task PlayAppend_APauseWhileItResolves_IsNotOverwritten()
    {
        StartPlaying();
        var fetch = new TaskCompletionSource<List<PlaylistItemDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.Setup(a => a.FetchPlaylistJsonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(fetch.Task);
        _reports.Clear();

        var play = _coordinator.PlayAsync(_server.ShokoUrl(VideoId + 1), append: true);
        await Until(() => _api.Invocations.Any(i => i.Method.Name == nameof(IShokoApiClient.FetchPlaylistJsonAsync)),
            "the playlist fetch");

        // The viewer pauses while the append is still resolving.
        await _coordinator.PauseAsync();
        Assert.Equal(PlaybackState.Paused, _coordinator.CurrentState);

        fetch.SetResult(Items(VideoId + 1));
        await play;

        // The append changed the queue, not what is playing: it says nothing
        // about the state, and the viewer's pause stands.
        Assert.Equal(PlaybackState.Paused, _coordinator.CurrentState);
        Assert.Equal(PlaybackState.Paused, _reports.Last().State);
        Assert.DoesNotContain(_reports, r => r.State is PlaybackState.Loading);
        Assert.DoesNotContain(_reports.SkipWhile(r => r.State is not PlaybackState.Paused),
            r => r.State is PlaybackState.Playing);
        _mpv.Verify(m => m.AppendListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PlayAppend_AResumeWhileItResolves_IsNotOverwritten()
    {
        StartPlaying();
        Property("pause", true);
        Assert.Equal(PlaybackState.Paused, _coordinator.CurrentState);
        var fetch = new TaskCompletionSource<List<PlaylistItemDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.Setup(a => a.FetchPlaylistJsonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(fetch.Task);
        _reports.Clear();

        var play = _coordinator.PlayAsync(_server.ShokoUrl(VideoId + 1), append: true);
        await Until(() => _api.Invocations.Any(i => i.Method.Name == nameof(IShokoApiClient.FetchPlaylistJsonAsync)),
            "the playlist fetch");

        await _coordinator.ResumeAsync();
        Assert.Equal(PlaybackState.Playing, _coordinator.CurrentState);

        fetch.SetResult(Items(VideoId + 1));
        await play;

        Assert.Equal(PlaybackState.Playing, _coordinator.CurrentState);
        Assert.Equal(PlaybackState.Playing, _reports.Last().State);
        Assert.DoesNotContain(_reports, r => r.State is PlaybackState.Loading);
    }

    // ── 4. A Stop always reports, and wins over a load ──

    [Fact]
    public async Task Stop_WhenMpvFailsToStop_StillReportsStopped()
    {
        StartPlaying();
        _mpv.Setup(m => m.StopAsync()).ThrowsAsync(new IOException("mpv went away"));
        _reports.Clear();

        await _coordinator.StopAsync();

        Assert.Equal(PlaybackState.Stopped, _coordinator.CurrentState);
        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(60.0, stopped.PositionSeconds, 3);
        Assert.False(_manager.HasActiveSession);
    }

    [Fact]
    public async Task Stop_DuringPlaysLoadingPhase_Wins()
    {
        var fetch = new TaskCompletionSource<List<PlaylistItemDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.Setup(a => a.FetchPlaylistJsonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(fetch.Task);

        var play = _coordinator.PlayAsync(_server.ShokoUrl(VideoId));
        await Until(() => _api.Invocations.Any(i => i.Method.Name == nameof(IShokoApiClient.FetchPlaylistJsonAsync)),
            "the playlist fetch");
        Assert.Equal(PlaybackState.Loading, _coordinator.CurrentState);

        await _coordinator.StopAsync();
        Assert.Equal(PlaybackState.Stopped, _coordinator.CurrentState);

        // The fetch the load was waiting on comes back after the Stop.
        fetch.SetResult(Items(VideoId));
        await play;

        Assert.Equal(PlaybackState.Stopped, _coordinator.CurrentState);
        Assert.Equal(PlaybackState.Stopped, _reports.Last().State);
        _mpv.Verify(m => m.LoadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mpv.Verify(m => m.LaunchAndConnectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 5. A pause at the very end is the end, and is reported as one ──

    [Fact]
    public async Task APauseWithin300msOfTheEnd_NothingQueued_ReportsStoppedAtTheDuration()
    {
        StartPlaying(positionSeconds: 1_419.8);
        var field = typeof(PlaybackCoordinator).GetField("_duration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field); // renamed: this test has to follow it
        field.SetValue(_coordinator, DurationMs);
        _reports.Clear();

        // The hub's Pause, 200 ms before the end. mpv pauses and says so.
        await _coordinator.PauseAsync();
        await Until(() => _coordinator.CurrentState is PlaybackState.Stopped, "Stopped");

        var stopped = Assert.Single(_reports, r => r.State is PlaybackState.Stopped);
        Assert.Equal(DurationMs / 1000, stopped.PositionSeconds, 3);
        Assert.DoesNotContain(_reports, r => r.State is PlaybackState.Paused);
        Assert.Equal(PlaybackState.Stopped, _reports.Last().State);
    }
}
