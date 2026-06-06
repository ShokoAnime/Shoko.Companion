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
                        "ParentSeries": 10,
                        "AniDB": 5000,
                        "TvDB": [11704219],
                        "IMDB": [],
                        "TMDB": {
                            "Episode": [7123783],
                            "Movie": [],
                            "Show": [288551]
                        },
                        "ID": 100
                    },
                    "HasCustomName": false,
                    "Description": "",
                    "IsFavorite": false,
                    "Images": {
                        "Posters": [],
                        "Backdrops": [],
                        "Banners": [],
                        "Logos": [],
                        "Discs": []
                    },
                    "Duration": "00:23:40.0630000",
                    "ResumePosition": null,
                    "WatchCount": 0,
                    "IsHidden": false,
                    "UserRating": null,
                    "Watched": null,
                    "Created": "2026-05-19T14:57:22.4888591Z",
                    "Updated": "2026-05-19T14:57:22.4888591Z",
                    "Name": "Episode 1",
                    "Size": 1
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
        Assert.Equal("Episode 1", ep.Name);
        Assert.NotNull(ep.IDs);
        Assert.Equal(100, ep.IDs.ID);
        Assert.Equal(5000, ep.IDs.AniDB);
        Assert.Equal(10, ep.IDs.ParentSeries);
        Assert.Single(ep.IDs.TvDB);
        Assert.Empty(ep.IDs.IMDB);
        Assert.NotNull(ep.IDs.TMDB);
        Assert.Single(ep.IDs.TMDB!.Episode);
        Assert.Empty(ep.IDs.TMDB.Movie);
        Assert.Single(ep.IDs.TMDB.Show);
        Assert.Equal(23, ep.Duration.Minutes);

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
