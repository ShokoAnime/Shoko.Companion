using Moq;
using Shoko.Companion.Configuration;
using Shoko.Companion.Launch;
using Shoko.Companion.Notifications;
using Shoko.Companion.Playback;
using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

[Collection("SharedSettings")]
public class FolderActionHandlerTests
{
    /// <summary>
    /// Every folder the handler asked the file manager to open. The handler
    /// under test never gets the real launcher: these tests use real folders
    /// that exist, so the real one would open a window on the desktop the
    /// suite runs on, once per test and per run.
    /// </summary>
    private readonly List<string> _opened = [];

    public FolderActionHandlerTests()
    {
        // Reset settings to a clean state for each test
        SettingsProvider.Instance.Settings.Connections.Clear();
    }

    private FolderActionHandler Handler()
        => new(new Mock<INotificationService>(MockBehavior.Strict).Object, _opened.Add);

    private static ServerConnection CreateTestConnection()
    {
        var conn = new ServerConnection
        {
            Name = "test-server",
            Routes = [new ConnectionRoute { BaseUrl = "server" }]
        };
        SettingsProvider.Instance.Settings.Connections.Add(conn);
        return conn;
    }

    [Fact]
    public void OpenFolder_WithMatchingId_ReturnsTrue()
    {
        var conn = CreateTestConnection();
        // OpenFolder verifies the local folder exists before opening it, so
        // materialize the mapped path (incl. the relative part) on disk.
        var localRoot = Path.Combine(Path.GetTempPath(), "shoko-companion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(localRoot, "Series"));
        conn.ManagedFolderMappings.Add(new ManagedFolderMapping
        {
            Id = 1,
            ServerPath = "/mnt/anime",
            LocalPath = localRoot
        });

        var parsed = new ParsedShokoUrl
        {
            Action = ShokoUrlAction.OpenFolderRelative,
            ServerBaseUrl = "http://server",
            ManagedFolderId = 1,
            RelativePath = "Series"
        };

        var handler = Handler();
        var result = handler.OpenFolder(parsed);

        Assert.True(result);
        Assert.Equal([Path.Combine(localRoot, "Series")], _opened);
    }

    [Fact]
    public void OpenFolder_WithMatchingIdNoRelativePath_ReturnsTrue()
    {
        var conn = CreateTestConnection();
        var localRoot = Path.Combine(Path.GetTempPath(), "shoko-companion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localRoot);
        conn.ManagedFolderMappings.Add(new ManagedFolderMapping
        {
            Id = 2,
            ServerPath = "/media",
            LocalPath = localRoot
        });

        var parsed = new ParsedShokoUrl
        {
            Action = ShokoUrlAction.OpenFolderRelative,
            ServerBaseUrl = "http://server",
            ManagedFolderId = 2
        };

        var handler = Handler();
        var result = handler.OpenFolder(parsed);

        Assert.True(result);
        Assert.Equal([localRoot], _opened);
    }

    [Fact]
    public void OpenFolder_WithUnknownId_ReturnsFalse()
    {
        var conn = CreateTestConnection();
        conn.ManagedFolderMappings.Add(new ManagedFolderMapping
        {
            Id = 1,
            ServerPath = "/mnt/anime",
            LocalPath = "/local/anime"
        });

        var parsed = new ParsedShokoUrl
        {
            Action = ShokoUrlAction.OpenFolderRelative,
            ServerBaseUrl = "http://server",
            ManagedFolderId = 999 // unknown
        };

        var handler = Handler();
        var result = handler.OpenFolder(parsed);

        Assert.False(result);
        Assert.Empty(_opened);
    }

    [Fact]
    public void OpenFolder_WithIgnoredId_ReturnsFalse()
    {
        var conn = CreateTestConnection();
        conn.IgnoredManagedFolderIds.Add(42);

        var parsed = new ParsedShokoUrl
        {
            Action = ShokoUrlAction.OpenFolderRelative,
            ServerBaseUrl = "http://server",
            ManagedFolderId = 42
        };

        var handler = Handler();
        var result = handler.OpenFolder(parsed);

        Assert.False(result);
        Assert.Empty(_opened);
    }

    [Fact]
    public void OpenFolder_WithNullId_ReturnsFalse()
    {
        var parsed = new ParsedShokoUrl
        {
            Action = ShokoUrlAction.OpenFolderRelative,
            ServerBaseUrl = "http://server",
            ManagedFolderId = null
        };

        var handler = Handler();
        var result = handler.OpenFolder(parsed);

        Assert.False(result);
        Assert.Empty(_opened);
    }

    [Fact]
    public void OpenFolder_WithEmptyLocalPath_ReturnsFalse()
    {
        var conn = CreateTestConnection();
        conn.ManagedFolderMappings.Add(new ManagedFolderMapping
        {
            Id = 1,
            ServerPath = "/mnt/anime",
            LocalPath = string.Empty
        });

        var parsed = new ParsedShokoUrl
        {
            Action = ShokoUrlAction.OpenFolderRelative,
            ServerBaseUrl = "http://server",
            ManagedFolderId = 1
        };

        var handler = Handler();
        var result = handler.OpenFolder(parsed);

        Assert.False(result);
        Assert.Empty(_opened);
    }

    // ── FindMatchingManagedFolder ───────────────────────────────────

    private static ManagedFolderDto Folder(int id, string path) =>
        new() { ID = id, Path = path, Name = $"Folder {id}" };

    [Fact]
    public void FindMatchingManagedFolder_ExactPath_ReturnsFolder()
    {
        var folders = new List<ManagedFolderDto> { Folder(1, "/mnt/anime") };
        var result = FolderActionHandler.FindMatchingManagedFolder(folders, "/mnt/anime/file.mkv");
        Assert.NotNull(result);
        Assert.Equal(1, result.ID);
    }

    [Fact]
    public void FindMatchingManagedFolder_LongestPrefix_Wins()
    {
        var folders = new List<ManagedFolderDto>
        {
            Folder(1, "/mnt/anime"),
            Folder(2, "/mnt/anime/series")
        };
        var result = FolderActionHandler.FindMatchingManagedFolder(folders, "/mnt/anime/series/show.mkv");
        Assert.NotNull(result);
        Assert.Equal(2, result.ID);
    }

    [Fact]
    public void FindMatchingManagedFolder_NoMatch_ReturnsNull()
    {
        var folders = new List<ManagedFolderDto> { Folder(1, "/media") };
        var result = FolderActionHandler.FindMatchingManagedFolder(folders, "/other/path/file.mkv");
        Assert.Null(result);
    }

    [Fact]
    public void FindMatchingManagedFolder_EmptyFolders_ReturnsNull()
    {
        var result = FolderActionHandler.FindMatchingManagedFolder(new List<ManagedFolderDto>(), "/mnt/anime/file.mkv");
        Assert.Null(result);
    }

    [Fact]
    public void FindMatchingManagedFolder_RootPath_Matches()
    {
        var folders = new List<ManagedFolderDto> { Folder(1, "/") };
        var result = FolderActionHandler.FindMatchingManagedFolder(folders, "/mnt/anime/file.mkv");
        Assert.NotNull(result);
        Assert.Equal(1, result.ID);
    }
}
