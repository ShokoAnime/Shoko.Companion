using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Newtonsoft.Json;
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
///   What an item is called is the media session server's answer, not this
///   client's own reading of Shoko's playlist.
///
///   <para>
///     The server resolves each item of a session once (and is moving that
///     answer behind a provider another plugin can supply, Kaname #309), and
///     every other client shows it. It sends the session's own connection
///     the unmasked description through <c>SessionStateChanged</c>, and the
///     registration returns one. These tests hold the three halves: the
///     wire binding, the session filter, and what the coordinator does with
///     the answer, in mpv's media title and the Discord presence, with
///     Shoko's playlist titles as the fallback for when the server gave none.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class ServerItemTitlesTests : IDisposable
{
    private const int VideoId = 29529;
    private const int NextVideoId = 29530;
    private const double DurationMs = 1_420_000;

    private readonly bool _discordEnabled;
    private readonly bool _startPaused;
    private readonly bool _configuredRoutes;
    private readonly string? _lastUsedServerUrl;

    private readonly FakeShokoServer _server = new();
    private readonly Mock<IMpvController> _mpv = new();
    private readonly Mock<IShokoApiClient> _api = new();
    private readonly PlaybackSessionManager _manager = new();
    private readonly PlaybackCoordinator _coordinator;

    private readonly ConcurrentQueue<string> _mediaTitles = new();
    private readonly ConcurrentQueue<DiscordPresenceData> _presences = new();

    private readonly JArray _mpvPlaylist = [];
    private long _mpvEntryIds = 100;
    private bool _mpvIdle = true;

    public ServerItemTitlesTests()
    {
        var s = SettingsProvider.Instance.Settings;
        _discordEnabled = s.DiscordEnabled;
        _startPaused = s.MpvStartPaused;
        _configuredRoutes = s.AlwaysUseConfiguredRoutes;
        _lastUsedServerUrl = s.LastUsedServerUrl;
        s.DiscordEnabled = false;
        s.MpvStartPaused = false;
        s.AlwaysUseConfiguredRoutes = false;
        _manager.MediaSessionConnected = true;

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
                    // What mpv takes from Shoko's EXTINF, which the fake
                    // server writes as "Episode {id}".
                    ["title"] = $"Episode {id}",
                    ["current"] = false,
                    ["id"] = ++_mpvEntryIds,
                });
            })
            .Returns(Task.CompletedTask);
        _mpv.Setup(m => m.SetPropertyAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((name, value, _) =>
            {
                if (name is "force-media-title")
                    _mediaTitles.Enqueue((string)value);
                if (name is "pause")
                    Property(name, value);
            })
            .Returns(Task.CompletedTask);

        _api.Setup(a => a.FetchPlaylistJsonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) => Items(int.Parse(url.Split("playlist=f")[1].Split('&')[0])));

        _coordinator = new PlaybackCoordinator(
            _api.Object, _mpv.Object, Mock.Of<INotificationService>(),
            Mock.Of<IDiscordPresenceService>(), _manager);
        _coordinator.MediaSessionId = Guid.NewGuid();

        _manager.DiscordPresenceChanged += (_, presence) =>
        {
            if (presence is not null)
                _presences.Enqueue(presence);
        };
    }

    public void Dispose()
    {
        _manager.EndSession(_manager.CurrentPositionMs);
        _server.Dispose();
        var s = SettingsProvider.Instance.Settings;
        s.DiscordEnabled = _discordEnabled;
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

    private static List<PlaylistItemDto> Items(int videoId)
        =>
        [
            new PlaylistItemDto
            {
                Episode = new PlaylistEpisodeDto { Title = "Shoko's episode title", Number = 1 },
                Parts = [new FileDto { ID = videoId, Duration = TimeSpan.FromMilliseconds(DurationMs) }],
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

    private async Task QueueAsync(params int[] videoIds)
    {
        Property("pause", true);
        foreach (var id in videoIds)
            await _coordinator.AddToPlaylistAsync([new PlaylistAddition(_server.ShokoUrl(id))], null);
    }

    /// <summary>
    ///   mpv moving onto a queued file and loading it, up to the session
    ///   being for that file.
    /// </summary>
    private async Task MpvLoadsAsync(int videoId)
    {
        _mpvIdle = false;
        foreach (var entry in _mpvPlaylist.Cast<JObject>())
            entry["current"] = entry.Value<string>("filename") == _server.StreamUrl(videoId);
        Property("playlist", _mpvPlaylist);
        Property("path", _server.StreamUrl(videoId));
        Event("file-loaded");
        await Until(() => _manager.CurrentVideoId == videoId, $"the session to be on {videoId}");
    }

    private static SessionItemInfoDto Item(int videoId, string series, string episode, int number = 3,
        string type = "episode")
        => new()
        {
            Id = Guid.NewGuid(),
            VideoId = videoId,
            SeriesTitle = series,
            EpisodeTitle = episode,
            EpisodeNumber = number,
            EpisodeType = type,
            EpisodeCount = 12,
        };

    private MediaSessionInfoDto Info(SessionItemInfoDto? current, SessionItemInfoDto? next = null)
        => new()
        {
            SessionId = _coordinator.MediaSessionId!.Value,
            CurrentItem = current,
            NextItem = next,
        };

    // ── The answer, as text ──

    [Theory]
    [InlineData("episode", 3, "Series - 3 - Episode")]
    [InlineData("special", 2, "Series - S2 - Episode")]
    [InlineData("credits", 1, "Series - C1 - Episode")]
    [InlineData(null, 3, "Series - 3 - Episode")]
    public void TheMediaTitle_HasShokosPlaylistShape(string? type, int number, string expected)
    {
        var titles = ItemTitles.FromSessionItem(new SessionItemInfoDto
        {
            SeriesTitle = "Series", EpisodeTitle = "Episode", EpisodeNumber = number, EpisodeType = type,
        });

        Assert.Equal(expected, titles?.MediaTitle);
    }

    [Fact]
    public void AnItemWithoutTitles_IsNoAnswer()
    {
        Assert.Null(ItemTitles.FromSessionItem(null));
        Assert.Null(ItemTitles.FromSessionItem(new SessionItemInfoDto { VideoId = VideoId, EpisodeNumber = 3 }));
        Assert.Null(ItemTitles.FromSessionItem(new SessionItemInfoDto { SeriesTitle = " ", EpisodeTitle = "" }));
    }

    [Fact]
    public void AnItemWithOneTitle_ShowsWhatItHas()
    {
        var titles = ItemTitles.FromSessionItem(new SessionItemInfoDto { SeriesTitle = "Series" });

        Assert.Equal("Series", titles?.MediaTitle);
        Assert.Null(titles?.EpisodeTitle);
    }

    // ── The wire ──

    /// <summary>
    ///   The plugin's <c>MediaSessionInfo</c> as the host's Newtonsoft
    ///   writes it: PascalCase, lowercase string enums, and many fields this
    ///   client does not read. Bound with the hub connection's own settings.
    /// </summary>
    [Fact]
    public void TheServersSessionDescription_Binds()
    {
        var sessionId = Guid.NewGuid();
        var json = $$"""
            {
              "SessionId": "{{sessionId}}",
              "DeviceInfo": { "Name": "mpv", "DeviceType": "companion" },
              "State": "playing",
              "Position": "00:01:00",
              "CurrentItem": {
                "Id": "5b1a6e0e-6b8e-5a43-9d7f-3e0f4c7b2a11",
                "IsPrivate": true,
                "Title": "Episode 29529",
                "MediaType": "video",
                "StreamUrl": "http://shoko/api/v3/File/29529/Stream",
                "VideoId": 29529,
                "PreviewStatus": "ready",
                "PlayMethod": "directPlay",
                "EpisodeTitle": "The Server's Episode",
                "EpisodeNumber": 2,
                "EpisodeType": "special",
                "EpisodeCount": 12,
                "SeriesTitle": "The Server's Series",
                "SeriesPoster": "/api/v3/Image/1"
              },
              "NextItem": null,
              "Capabilities": { "CanSeek": true }
            }
            """;

        var options = new NewtonsoftJsonHubProtocolOptions();
        MediaSessionClient.ConfigureHubPayload(options);
        var info = JsonSerializer.Create(options.PayloadSerializerSettings)
            .Deserialize<MediaSessionInfoDto>(new JsonTextReader(new StringReader(json)));

        Assert.NotNull(info);
        Assert.Equal(sessionId, info.SessionId);
        Assert.Null(info.NextItem);
        var item = Assert.IsType<SessionItemInfoDto>(info.CurrentItem);
        Assert.True(item.IsPrivate);
        Assert.Equal(29529, item.VideoId);
        Assert.Equal("The Server's Series", item.SeriesTitle);
        Assert.Equal("The Server's Episode", item.EpisodeTitle);
        Assert.Equal("special", item.EpisodeType);
        Assert.Equal("The Server's Series - S2 - The Server's Episode", ItemTitles.FromSessionItem(item)?.MediaTitle);
    }

    /// <summary>
    ///   Only this session's own description is taken. One for another
    ///   session, or one arriving with no session held, is not ours to show.
    /// </summary>
    [Fact]
    public async Task OnlyThisSessionsDescription_ReachesTheCoordinator()
    {
        var coordinator = new StubPlaybackCoordinator();
        await using var client = new MediaSessionClient(
            "http://localhost:8111", "apikey", "device", coordinator);
        var own = Guid.NewGuid();

        await client.ApplyOwnSessionInfoAsync(new MediaSessionInfoDto { SessionId = own });
        Assert.Empty(coordinator.AppliedSessionInfo);

        client.SetSessionId(own);
        await client.ApplyOwnSessionInfoAsync(new MediaSessionInfoDto { SessionId = Guid.NewGuid() });
        await client.ApplyOwnSessionInfoAsync(null);
        Assert.Empty(coordinator.AppliedSessionInfo);

        await client.ApplyOwnSessionInfoAsync(new MediaSessionInfoDto { SessionId = own });
        Assert.Equal(own, Assert.Single(coordinator.AppliedSessionInfo).SessionId);
    }

    // ── What the coordinator shows ──

    /// <summary>
    ///   The answer arrives after the file loaded, since the server learns the
    ///   item from this client's report. Until then Shoko's playlist title
    ///   stands; then the window and the presence take the server's.
    /// </summary>
    [Fact]
    public async Task TheItemPlaying_IsRenamedToTheServersAnswer()
    {
        await QueueAsync(VideoId);
        await _coordinator.ResumeAsync();
        await MpvLoadsAsync(VideoId);
        await Until(() => _mediaTitles.Contains("Episode 29529"), "Shoko's playlist title");

        await _coordinator.ApplySessionInfoAsync(Info(Item(VideoId, "The Server's Series", "The Server's Episode")));

        Assert.Equal("The Server's Series - 3 - The Server's Episode", _mediaTitles.Last());
        Assert.Equal(("The Server's Series", "The Server's Episode"), _manager.CurrentTitles);
        await Until(() => _presences.Any(p => p.Details == "Watching The Server's Series"), "the presence");
        Assert.Contains(_presences, p => p.State == "The Server's Episode — Episode 3 of 12");

        // The same answer again changes nothing.
        var shown = _mediaTitles.Count;
        await _coordinator.ApplySessionInfoAsync(Info(Item(VideoId, "The Server's Series", "The Server's Episode")));
        Assert.Equal(shown, _mediaTitles.Count);
    }

    /// <summary>
    ///   The next item is described before it plays, so when it does the
    ///   server's answer is the first title shown, not a correction.
    /// </summary>
    [Fact]
    public async Task TheNextItem_PlaysUnderTheServersAnswer()
    {
        await QueueAsync(VideoId, NextVideoId);
        await _coordinator.ResumeAsync();
        await MpvLoadsAsync(VideoId);
        await _coordinator.ApplySessionInfoAsync(Info(
            Item(VideoId, "Series", "First"), Item(NextVideoId, "Series", "Second", number: 4)));

        _mediaTitles.Clear();
        await MpvLoadsAsync(NextVideoId);
        await Until(() => !_mediaTitles.IsEmpty, "a media title");

        Assert.Equal(["Series - 4 - Second"], _mediaTitles);
        Assert.Equal(("Series", "Second"), _manager.CurrentTitles);
    }

    /// <summary>
    ///   An answer about a file other than the one playing names nothing on
    ///   screen: the previous file's can land after the next has started.
    /// </summary>
    [Fact]
    public async Task AnAnswerForAnotherFile_RenamesNothing()
    {
        await QueueAsync(VideoId);
        await _coordinator.ResumeAsync();
        await MpvLoadsAsync(VideoId);
        await Until(() => !_mediaTitles.IsEmpty, "a media title");
        _mediaTitles.Clear();

        await _coordinator.ApplySessionInfoAsync(Info(Item(NextVideoId, "Other", "Other")));

        Assert.Empty(_mediaTitles);
        Assert.NotEqual("Other", _manager.CurrentTitles.Series);
    }

    /// <summary>
    ///   An item the server gave no titles for keeps Shoko's playlist title,
    ///   and so does every item once the session is gone: the server's names
    ///   last only while it is the one answering.
    /// </summary>
    [Fact]
    public async Task WithoutTheServersAnswer_ShokosPlaylistTitleStands()
    {
        await QueueAsync(VideoId);
        await _coordinator.ApplySessionInfoAsync(Info(new SessionItemInfoDto { VideoId = VideoId }));
        await _coordinator.ResumeAsync();
        await MpvLoadsAsync(VideoId);
        await Until(() => !_mediaTitles.IsEmpty, "a media title");
        Assert.Equal(["Episode 29529"], _mediaTitles);

        // Answered, then the session goes away before the file plays again.
        await _coordinator.ApplySessionInfoAsync(Info(Item(VideoId, "Series", "Episode")));
        _coordinator.MediaSessionId = null;
        _mediaTitles.Clear();
        Event("file-loaded");
        await Until(() => !_mediaTitles.IsEmpty, "a media title");

        Assert.Equal(["Episode 29529"], _mediaTitles);
    }
}
