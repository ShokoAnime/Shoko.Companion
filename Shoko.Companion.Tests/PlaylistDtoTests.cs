using Newtonsoft.Json;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

public class PlaylistDtoTests
{
    [Fact]
    public void Deserialize_PlaylistJson_WithMultipleItems_Succeeds()
    {
        var json = """
        [
            {
                "Episode": {
                    "IDs": {
                        "AnidbEpisode": 100,
                        "AnidbAnime": 10,
                        "ShokoEpisode": 999,
                        "ShokoSeries": 888,
                        "TmdbShow": 288551,
                        "TmdbMovie": null,
                        "TvdbShow": 11704219,
                        "ImdbMovie": null
                    },
                    "Title": "Episode 1",
                    "Number": 1,
                    "Type": "Episode",
                    "Size": 1,
                    "SeriesTitle": "My Series"
                },
                "AdditionalEpisodes": [],
                "Parts": [
                    {
                        "ID": 42,
                        "Size": 734003200,
                        "IsVariation": false,
                        "IsIgnored": false,
                        "Hashes": [
                            { "Type": "ED2K", "Value": "abcdef1234567890abcdef1234567890" }
                        ],
                        "Locations": [
                            {
                                "ID": 1,
                                "FileID": 42,
                                "ManagedFolderID": 2,
                                "RelativePath": "anime/ep1.mkv",
                                "IsAccessible": true
                            }
                        ],
                        "AVDump": {
                            "Status": null,
                            "LastDumpedAt": null,
                            "LastVersion": null
                        },
                        "Resolution": "1080p",
                        "Duration": "00:23:30.0000000",
                        "ResumePosition": "00:05:30.0000000",
                        "Viewed": null,
                        "Watched": null,
                        "Imported": null,
                        "Created": "2026-06-01T15:08:49.9881533Z",
                        "Updated": "2026-06-01T15:09:00.087303Z"
                    }
                ]
            }
        ]
        """;

        var items = JsonConvert.DeserializeObject<List<PlaylistItemDto>>(json);
        Assert.NotNull(items);
        Assert.Single(items);

        var item = items![0];
        Assert.NotNull(item.Episode);
        Assert.Empty(item.AdditionalEpisodes);
        Assert.Single(item.Parts);

        var ep = item.Episode!;
        Assert.Equal("Episode 1", ep.Title);
        Assert.Equal(1, ep.Number);
        Assert.Equal("My Series", ep.SeriesTitle);
        Assert.NotNull(ep.IDs);
        Assert.Equal(100, ep.IDs.AnidbEpisode);
        Assert.Equal(10, ep.IDs.AnidbAnime);
        Assert.Equal(999, ep.IDs.ShokoEpisode);
        Assert.Equal(288551, ep.IDs.TmdbShow);
        Assert.Null(ep.IDs.TmdbMovie);
        Assert.Equal(11704219, ep.IDs.TvdbShow);
        Assert.Null(ep.IDs.ImdbMovie);

        var file = item.Parts[0];
        Assert.Equal(42, file.ID);
        Assert.Equal(734003200, file.Size);
        Assert.Equal(23, file.Duration.Minutes);
        Assert.NotNull(file.ResumePosition);
        Assert.Equal(5, file.ResumePosition.Value.Minutes);
        Assert.Null(file.Watched);
        Assert.Equal("1080p", file.Resolution);

        Assert.NotNull(file.Hashes);
        Assert.Single(file.Hashes);
        Assert.Equal("ED2K", file.Hashes[0].Type);

        Assert.NotNull(file.Locations);
        Assert.Equal("anime/ep1.mkv", file.Locations[0].RelativePath);
        Assert.True(file.Locations[0].IsAccessible);

        Assert.NotNull(file.AVDump);
        Assert.Null(file.AVDump!.Status);
    }

    [Fact]
    public void Deserialize_FileUserData_Succeeds()
    {
        var json = """
        {
            "progressPosition": "00:12:34.5670000",
            "watchedCount": 3,
            "lastWatchedAt": "2026-05-15T20:30:00Z",
            "lastUpdatedAt": "2026-05-15T20:30:00Z"
        }
        """;

        var data = JsonConvert.DeserializeObject<VideoUserDataDto>(json);
        Assert.NotNull(data);
        Assert.NotNull(data!.ProgressPosition);
        Assert.Equal(12, data.ProgressPosition.Value.Minutes);
        Assert.Equal(34, data.ProgressPosition.Value.Seconds);
        Assert.Equal(3, data.WatchedCount);
        Assert.NotNull(data.LastWatchedAt);
        Assert.Equal(2026, data.LastWatchedAt.Value.Year);
    }
}
