using System.Net;
using Moq;
using Moq.Protected;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

public class ShokoApiClientTests
{
    [Fact]
    public void BuildStreamUrl_ReturnsCorrectUrl()
    {
        var handler = new Mock<HttpMessageHandler>();
        var client = new ShokoApiClient(new HttpClient(handler.Object));
        client.SetBaseUrl("http://localhost:8111");
        var url = client.BuildStreamUrl(42);
        Assert.Contains("/api/v3/File/42/Stream", url);
        // ApiKey is null in tests (not configured via SettingsProvider), so no apikey query param
        Assert.DoesNotContain("apikey=null", url);
    }

    [Fact]
    public async Task ScrobbleAsync_OnSuccess_ReturnsTrue()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Accepted));

        var client = new ShokoApiClient(new HttpClient(handler.Object));
        client.SetBaseUrl("http://localhost:8111");
        var result = await client.ScrobbleAsync(42, ScrobbleEventType.PlaybackStart, null, null);
        Assert.True(result);
    }

    [Fact]
    public async Task ScrobbleAsync_OnFailure_ReturnsFalse()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.BadRequest));

        var client = new ShokoApiClient(new HttpClient(handler.Object));
        client.SetBaseUrl("http://localhost:8111");
        var result = await client.ScrobbleAsync(42, ScrobbleEventType.PlaybackEnd, TimeSpan.FromSeconds(30), true);
        Assert.False(result);
    }

    [Fact]
    public async Task FetchFileUserDataAsync_WhenFileMissing_ReturnsNull()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        var client = new ShokoApiClient(new HttpClient(handler.Object));
        client.SetBaseUrl("http://localhost:8111");
        var result = await client.FetchFileUserDataAsync(999);
        Assert.Null(result);
    }
}
