using Shoko.Companion.Configuration;
using Shoko.Companion.Launch;
using Xunit;

namespace Shoko.Companion.Tests;

[Collection("SharedSettings")]
public class ShokoUrlParserTests
{
    public ShokoUrlParserTests()
    {
        // Reset settings to a clean state for each test
        SettingsProvider.Instance.Settings.Connections.Clear();
    }

    /// <summary>
    /// Helper: add a connection with a single route and API key for testing connection lookups.
    /// </summary>
    private static void SetupConnection(string routeKey, string apiKey, bool useHttps = false)
    {
        var conn = new ServerConnection
        {
            Name = routeKey.Split('/')[0],
            Routes = [new ConnectionRoute { BaseUrl = routeKey, UseHttps = useHttps }],
            ApiKey = apiKey
        };
        SettingsProvider.Instance.Settings.Connections.Add(conn);
    }

    // ── With explicit protocol ─────────────────────────────────────

    [Fact]
    public void Parse_NewPlay_WithHttp_ExtractsFields()
    {
        var result = ShokoUrlParser.Parse(
            "shoko:http://myserver/play?playlist=s1");

        Assert.NotNull(result);
        Assert.Equal(ShokoUrlAction.Play, result.Action);
        Assert.Equal("http://myserver", result.ServerBaseUrl);
        Assert.Equal("s1", result.PlaylistId);
    }

    [Fact]
    public void Parse_NewPlay_WithHttps_NoApiKey()
    {
        SetupConnection("secure-host:8443", "stored-key", useHttps: true);
        var result = ShokoUrlParser.Parse(
            "shoko:https://secure-host:8443/play?playlist=e42");

        Assert.NotNull(result);
        Assert.Equal(ShokoUrlAction.Play, result.Action);
        Assert.Equal("https://secure-host:8443", result.ServerBaseUrl);
        Assert.Equal("e42", result.PlaylistId);
    }

    // ── New format without protocol ────────────────────────────────

    // Note: protocol probing requires network; these tests assume "http" fallback
    // when the HTTPS probe fails (no server listening). The actual protocol is
    // environment-dependent; we validate at least the structure is correct.

    [Fact]
    public void Parse_NewPlay_WithoutProtocol_ReturnsHttpFallback()
    {
        SetupConnection("myserver", "stored-key");
        // No server at test-time: parser falls back to http
        var result = ShokoUrlParser.Parse(
            "shoko:myserver/play?playlist=s1");

        Assert.NotNull(result);
        Assert.Equal(ShokoUrlAction.Play, result.Action);
        Assert.Equal("http://myserver", result.ServerBaseUrl);
        Assert.Equal("s1", result.PlaylistId);
    }

    [Fact]
    public void Parse_NewPlay_WithPort_WithoutProtocol()
    {
        var result = ShokoUrlParser.Parse(
            "shoko:server:8111/play?playlist=s1");

        Assert.NotNull(result);
        Assert.Equal(ShokoUrlAction.Play, result.Action);
        Assert.Equal("http://server:8111", result.ServerBaseUrl);
    }

    // ── Open folder ────────────────────────────────────────────────

    [Fact]
    public void Parse_WithDoubleSlash_StripsBothSlashes()
    {
        SetupConnection("server", "k");
        var result = ShokoUrlParser.Parse(
            "shoko://http://server/play?playlist=s1");
        Assert.NotNull(result);
        Assert.Equal(ShokoUrlAction.Play, result.Action);
        Assert.Equal("http://server", result.ServerBaseUrl);
    }

    [Fact]
    public void Parse_OpenFolder_WithProtocol()
    {
        var result = ShokoUrlParser.Parse(
            "shoko:http://server/open-folder?managedFolder=3");

        Assert.NotNull(result);
        Assert.Equal(ShokoUrlAction.OpenFolderRelative, result.Action);
        Assert.Equal(3, result.ManagedFolderId);
        Assert.Null(result.RelativePath);
        Assert.Equal("http://server", result.ServerBaseUrl);
    }

    [Fact]
    public void Parse_OpenFolder_WithRelativePath()
    {
        var result = ShokoUrlParser.Parse(
            "shoko:server/open-folder?managedFolder=5&relativePath=Series/Show");

        Assert.NotNull(result);
        Assert.Equal(ShokoUrlAction.OpenFolderRelative, result.Action);
        Assert.Equal(5, result.ManagedFolderId);
        Assert.Equal("Series/Show", result.RelativePath);
    }

    // ── Sub-path support ───────────────────────────────────────────

    [Fact]
    public void Parse_NewPlay_WithDeepSubPath()
    {
        SetupConnection("server/proxy/v2/client", "stored-key");
        var result = ShokoUrlParser.Parse(
            "shoko:http://server/proxy/v2/client/play?playlist=s1");

        Assert.NotNull(result);
        Assert.Equal(ShokoUrlAction.Play, result.Action);
        Assert.Equal("http://server/proxy/v2/client", result.ServerBaseUrl);
        Assert.Equal("s1", result.PlaylistId);
    }

    [Fact]
    public void Parse_OpenFolder_WithSubPath()
    {
        var result = ShokoUrlParser.Parse(
            "shoko:http://server/tunnel/mount/open-folder?managedFolder=7");

        Assert.NotNull(result);
        Assert.Equal(ShokoUrlAction.OpenFolderRelative, result.Action);
        Assert.Equal(7, result.ManagedFolderId);
        Assert.Equal("http://server/tunnel/mount", result.ServerBaseUrl);
    }

    [Fact]
    public void Parse_Play_NoKeyAnywhere_ReturnsParsedUrl()
    {
        SettingsProvider.Instance.Settings.Connections.Clear();
        var result = ShokoUrlParser.Parse("shoko:http://server/play?playlist=s1");
        Assert.NotNull(result);
        Assert.Equal("http://server", result.ServerBaseUrl);
    }

    // ── Error cases ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-shoko-url")]
    [InlineData("http://bare-http")]
    [InlineData("shoko:")]
    [InlineData("shoko:   ")]
    public void Parse_Invalid_ReturnsUnknown(string? input)
    {
        Assert.Equal(ShokoUrlAction.Unknown, ShokoUrlParser.Parse(input!).Action);
    }

    [Fact]
    public void Parse_MissingAction_ReturnsUnknown()
    {
        Assert.Equal(ShokoUrlAction.Unknown, ShokoUrlParser.Parse("shoko:http://server").Action);
    }

    [Fact]
    public void Parse_UnknownAction_ReturnsUnknown()
    {
        Assert.Equal(ShokoUrlAction.Unknown, ShokoUrlParser.Parse("shoko:http://server/unknown?x=1").Action);
    }

}
