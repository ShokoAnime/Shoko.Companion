using Newtonsoft.Json;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
/// Detection reads the server's <c>media-sessions</c> feature, and the
/// hub path comes from it rather than from a constant here.
/// </summary>
public class MediaSessionDetectionTests
{
    private static List<PluginFeatureDto> Parse(string json)
        => JsonConvert.DeserializeObject<List<PluginFeatureDto>>(json)!;

    [Fact]
    public void ReadsThePathsFromTheFeature()
    {
        // The shape the host serves, as it serves it.
        var features = Parse("""
            [
              { "PluginID": "11111111-2222-3333-4444-555555555555", "Name": "other", "Version": "1.0.0", "Visibility": "Anonymous", "Metadata": null },
              { "PluginID": "d4a2e8b1-7c3f-4a5d-9e6b-0f1c2d3e4a5b", "Name": "media-sessions", "Version": "1.2.0", "Visibility": "Anonymous",
                "Metadata": { "HubPath": "/signalr/somewhere/v1", "ApiPath": "/api/somewhere/v1/", "CanServerCaptureScreenshot": true, "CanPlayRemoteSources": false } }
            ]
            """);

        var feature = MediaSessionDetection.Find(features);

        Assert.NotNull(feature);
        Assert.Equal("/signalr/somewhere/v1", feature.HubPath);
        Assert.Equal("/api/somewhere/v1", feature.ApiPath);
        Assert.Equal(new Guid("d4a2e8b1-7c3f-4a5d-9e6b-0f1c2d3e4a5b"), feature.PluginId);
    }

    [Fact]
    public void NoFeatureMeansNoMediaSessions()
    {
        Assert.Null(MediaSessionDetection.Find(Parse("[]")));
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("0.9.0")]
    [InlineData("nonsense")]
    public void AnotherMajorVersionIsNotRead(string version)
    {
        var features = new List<PluginFeatureDto>
        {
            new()
            {
                Name = MediaSessionDetection.FeatureName,
                Version = version,
                Metadata = new() { ["HubPath"] = "/hub", ["ApiPath"] = "/api" },
            },
        };

        Assert.Null(MediaSessionDetection.Find(features));
    }

    [Fact]
    public void AFeatureWithoutAHubPathIsNotRead()
    {
        var features = new List<PluginFeatureDto>
        {
            new()
            {
                Name = MediaSessionDetection.FeatureName,
                Version = "1.0.0",
                Metadata = new() { ["ApiPath"] = "/api" },
            },
        };

        Assert.Null(MediaSessionDetection.Find(features));
    }
}
