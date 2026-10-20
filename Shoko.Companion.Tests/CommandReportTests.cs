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
    private long _mpvEntryIds = 100;
    private readonly int _skipEventCount;

    /// <summary>A state report as <c>App</c> builds it.</summary>
    private sealed record Report(PlaybackState State, double PositionSeconds, int? ItemVideoId, PlaybackTrackSelectionDto? Tracks, string? Error = null);

    public CommandReportTests()
    {
        var s = SettingsProvider.Instance.Settings;
        _syncingEnabled = s.PlaybackSyncingEnabled;
        _discordEnabled = s.DiscordEnabled;
        _privacyMode = s.PrivacyMode;
        _startPaused = s.MpvStartPaused;
        _configuredRoutes = s.AlwaysUseConfiguredRoutes;
        _lastUsedServerUrl = s.LastUsedServerUrl;
        _skipEventCount = s.SyncUserDataInitialSkipEventCount;
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
                    // mpv numbers its entries itself, in its own sequence.
                    ["id"] = ++_mpvEntryIds,
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
            _coordinator.CurrentItem?.VideoId, _coordinator.CurrentTracks, e.ErrorMessage));
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
        s.SyncUserDataInitialSkipEventCount = _skipEventCount;
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

    // ── 6. A file mpv cannot play ends in Error, from mpv's own end-file ──

    /// <summary>
    ///   An mpv event as <c>MpvIpcClient</c> hands it on: the whole
    ///   message, since mpv writes an event's fields at the top level.
    /// </summary>
    private static JObject MpvMessage(string name, object fields)
    {
        var message = JObject.FromObject(fields);
        message["event"] = name;
        return message;
    }

    /// <summary>mpv's number for the entry playing <paramref name="videoId"/>.</summary>
    private long EntryIdOf(int videoId)
        => _mpvPlaylist.Cast<JObject>()
            .First(e => e.Value<string>("filename") == _server.StreamUrl(videoId))
            .Value<long>("id");

    /// <summary>
    ///   What mpv does when it moves onto a file: the playlist's current
    ///   entry moves, it says it is starting the entry, and <c>path</c>
    ///   names the file.
    /// </summary>
    private void MpvStarts(int videoId)
    {
        _mpvIdle = false;
        foreach (var entry in _mpvPlaylist.Cast<JObject>())
            entry["current"] = entry.Value<string>("filename") == _server.StreamUrl(videoId);
        Property("playlist", _mpvPlaylist);
        Event("start-file", MpvMessage("start-file", new { playlist_entry_id = EntryIdOf(videoId) }));
        Property("path", _server.StreamUrl(videoId));
    }

    /// <summary>mpv failing to play the entry numbered <paramref name="entryId"/>.</summary>
    private void MpvFails(long entryId, string error = "loading failed")
        => Event("end-file", MpvMessage("end-file", new { reason = "error", playlist_entry_id = entryId, file_error = error }));

    /// <summary>Several videos queued through the hub into an idle mpv.</summary>
    private async Task QueueInIdleMpvAsync(params int[] videoIds)
    {
        Property("pause", true);
        await _coordinator.AddToPlaylistAsync(
            videoIds.Select(id => new PlaylistAddition(_server.ShokoUrl(id))).ToList(), null);
        Assert.Equal(videoIds.Length, _coordinator.CurrentPlaylist.Count);
    }

    /// <summary>
    ///   That the last thing reported is an Error carrying mpv's message,
    ///   and that nothing after the Error said preparing, waiting out a
    ///   <c>file-loaded</c> delay and mpv going idle first.
    /// </summary>
    private async Task AssertEndsInErrorAsync(string message)
    {
        await Until(() => _coordinator.CurrentState is PlaybackState.Error, "Error");
        // mpv goes idle with nothing left to play, and a file-loaded delay
        // passes: neither is a reason to say anything else.
        _mpvIdle = true;
        Property("idle-active", true);
        await Task.Delay(400);

        Assert.Equal(PlaybackState.Error, _coordinator.CurrentState);
        var error = Assert.Single(_reports, r => r.State is PlaybackState.Error);
        Assert.Equal(message, error.Error);
        Assert.DoesNotContain(_reports.SkipWhile(r => r.State is not PlaybackState.Error),
            r => r.State is PlaybackState.Loading);
        Assert.Equal(PlaybackState.Error, _reports.Last().State);
        Assert.False(_manager.HasActiveSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APlaylistJumpOrResumeFromIdle_WhoseFileFails_ReportsError(bool resume)
    {
        await QueueInIdleMpvAsync(VideoId);

        if (resume)
            await _coordinator.ResumeAsync();
        else
            await _coordinator.JumpToPlaylistItemAsync(_server.StreamUrl(VideoId));
        Assert.Equal(PlaybackState.Loading, _coordinator.CurrentState);

        MpvStarts(VideoId);
        MpvFails(EntryIdOf(VideoId), "unrecognized file format");

        await AssertEndsInErrorAsync("unrecognized file format");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayAsync_WhoseFileFails_ReportsError(bool failsBeforeLoadfileReturns)
    {
        // mpv expands the .m3u8 into its entries; the first one is the file.
        void MpvLoadsThePlaylistAndFails()
        {
            _mpvPlaylist.Add(new JObject
            {
                ["filename"] = _server.StreamUrl(VideoId),
                ["title"] = $"Episode {VideoId}",
                ["current"] = false,
                ["id"] = ++_mpvEntryIds,
            });
            MpvStarts(VideoId);
            MpvFails(EntryIdOf(VideoId));
        }

        if (failsBeforeLoadfileReturns)
        {
            // mpv's events race the reply to loadfile: the Play is still
            // awaiting it when the file has already failed, and must not
            // report Loading over the Error when it resumes.
            _mpv.Setup(m => m.LoadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback(MpvLoadsThePlaylistAndFails)
                .Returns(Task.CompletedTask);
        }

        await _coordinator.PlayAsync(_server.ShokoUrl(VideoId));
        _mpv.Verify(m => m.LoadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        if (!failsBeforeLoadfileReturns)
        {
            Assert.Equal(PlaybackState.Loading, _coordinator.CurrentState);
            MpvLoadsThePlaylistAndFails();
        }

        await AssertEndsInErrorAsync("loading failed");
    }

    [Fact]
    public async Task AnErrorFromAnAbandonedLoad_DoesNotOverwriteStopped()
    {
        await QueueInIdleMpvAsync(VideoId);
        await _coordinator.JumpToPlaylistItemAsync(_server.StreamUrl(VideoId));
        MpvStarts(VideoId);

        await _coordinator.StopAsync();
        Assert.Equal(PlaybackState.Stopped, _coordinator.CurrentState);

        // The file mpv was loading when the Stop came fails afterwards.
        MpvFails(EntryIdOf(VideoId));
        await Task.Delay(100);

        Assert.Equal(PlaybackState.Stopped, _coordinator.CurrentState);
        Assert.Equal(PlaybackState.Stopped, _reports.Last().State);
        Assert.DoesNotContain(_reports, r => r.State is PlaybackState.Error);
    }

    [Fact]
    public async Task AnErrorFromAnAbandonedLoad_DoesNotEndTheNewerOne()
    {
        await QueueInIdleMpvAsync(VideoId, VideoId + 1);
        await _coordinator.JumpToPlaylistItemAsync(_server.StreamUrl(VideoId));
        MpvStarts(VideoId);
        var abandoned = EntryIdOf(VideoId);

        // A Stop, then a jump to the other entry, which mpv starts.
        await _coordinator.StopAsync();
        _mpvPlaylist = []; // the Stop took that mpv down
        await QueueInIdleMpvAsync(VideoId, VideoId + 1);
        await _coordinator.JumpToPlaylistItemAsync(_server.StreamUrl(VideoId + 1));
        MpvStarts(VideoId + 1);
        _reports.Clear();

        MpvFails(abandoned);
        await Task.Delay(100);

        Assert.Equal(PlaybackState.Loading, _coordinator.CurrentState);
        Assert.DoesNotContain(_reports, r => r.State is PlaybackState.Error);
    }

    [Fact]
    public async Task APlayStillLoading_IsEndedByNothingButItsOwnError()
    {
        await QueueInIdleMpvAsync(VideoId);
        await _coordinator.JumpToPlaylistItemAsync(_server.StreamUrl(VideoId));
        MpvStarts(VideoId);
        _reports.Clear();

        // A slow load is idle too, and says so.
        _mpvIdle = true;
        Property("idle-active", true);
        // Ends that are not a failure of this file: a redirect, a stop.
        Event("end-file", MpvMessage("end-file", new { reason = "redirect", playlist_entry_id = EntryIdOf(VideoId) }));
        Event("end-file", MpvMessage("end-file", new { reason = "stop", playlist_entry_id = 7 }));
        // An error for an entry mpv is not loading.
        MpvFails(7);
        await Task.Delay(400);

        Assert.Equal(PlaybackState.Loading, _coordinator.CurrentState);
        Assert.Empty(_reports);

        // Its own error ends it.
        MpvFails(EntryIdOf(VideoId));
        await AssertEndsInErrorAsync("loading failed");
    }

    [Fact]
    public async Task AFailedFileInAPlaylist_EndsItsOwnSession_AndMpvMovesOnToTheNext()
    {
        await QueueInIdleMpvAsync(VideoId, VideoId + 1, VideoId + 2);
        await _coordinator.JumpToPlaylistItemAsync(_server.StreamUrl(VideoId));
        await MpvLoadsAsync(VideoId);
        await Until(() => _coordinator.CurrentState is PlaybackState.Playing, "Playing");
        Property("time-pos", 30.0);
        _reports.Clear();

        // mpv moves on to the second file, which it cannot play.
        MpvStarts(VideoId + 1);
        Assert.Equal(VideoId + 1, _manager.CurrentVideoId);
        MpvFails(EntryIdOf(VideoId + 1));

        await Until(() => _coordinator.CurrentState is PlaybackState.Error, "Error");
        Assert.False(_manager.HasActiveSession);
        Assert.Equal("loading failed", Assert.Single(_reports, r => r.State is PlaybackState.Error).Error);

        // mpv advances by itself after an error, and the next file starts
        // the way any file does.
        MpvStarts(VideoId + 2);
        Event("file-loaded");
        await Until(() => _manager.HasActiveSession, "the next file's session");
        await Until(() => _coordinator.CurrentState is PlaybackState.Playing, "Playing");

        Assert.Equal(VideoId + 2, _manager.CurrentVideoId);
        Assert.DoesNotContain(_reports, r => r.State is PlaybackState.Loading);
    }

    [Fact]
    public async Task AFileThatNeverPlayed_GetsNoPlaybackEndScrobble()
    {
        // No media session: the companion's own scrobbling is on.
        _manager.MediaSessionConnected = false;
        var s = SettingsProvider.Instance.Settings;
        s.PlaybackSyncingEnabled = true;
        s.SyncUserDataInitialSkipEventCount = 0;
        var scrobbles = new ConcurrentQueue<ScrobbleRequestEventArgs>();
        _manager.ScrobbleRequested += (_, e) => scrobbles.Enqueue(e);

        await QueueInIdleMpvAsync(VideoId, VideoId + 1);
        await _coordinator.JumpToPlaylistItemAsync(_server.StreamUrl(VideoId));
        await MpvLoadsAsync(VideoId);
        Property("time-pos", 30.0);

        // The second file never loads, and nothing is queued after it.
        MpvStarts(VideoId + 1);
        MpvFails(EntryIdOf(VideoId + 1));
        // mpv goes idle, with nothing after it. Before, this was where the
        // session ended: as a Stop, with a PlaybackEnd for the failed file.
        _mpvIdle = true;
        Property("idle-active", true);
        await Task.Delay(400); // scrobbles are raised on the thread pool

        Assert.DoesNotContain(scrobbles, e => e.FileId == VideoId + 1);
        Assert.DoesNotContain(scrobbles, e => e.EventType is ScrobbleEventType.PlaybackEnd);
        await AssertEndsInErrorAsync("loading failed");
    }
}
