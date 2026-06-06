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
    public FolderActionHandlerTests()
    {
        // Reset settings to a clean state for each test
        SettingsProvider.Instance.Settings.Connections.Clear();
    }

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
        conn.ManagedFolderMappings.Add(new ManagedFolderMapping
        {
            Id = 1,
            ServerPath = "/mnt/anime",
            LocalPath = "/home/user/media/anime"
        });

        var parsed = new ParsedShokoUrl
        {
            Action = ShokoUrlAction.OpenFolderRelative,
            ServerBaseUrl = "http://server",
            ManagedFolderId = 1,
            RelativePath = "Series/Show"
        };

        var handler = new FolderActionHandler(new Mock<INotificationService>(MockBehavior.Strict).Object);
        var result = handler.OpenFolder(parsed);

        Assert.True(result);
    }

    [Fact]
    public void OpenFolder_WithMatchingIdNoRelativePath_ReturnsTrue()
    {
        var conn = CreateTestConnection();
        conn.ManagedFolderMappings.Add(new ManagedFolderMapping
        {
            Id = 2,
            ServerPath = "/media",
            LocalPath = "D:\\Media\\Anime"
        });

        var parsed = new ParsedShokoUrl
        {
            Action = ShokoUrlAction.OpenFolderRelative,
            ServerBaseUrl = "http://server",
            ManagedFolderId = 2
        };

        var handler = new FolderActionHandler(new Mock<INotificationService>(MockBehavior.Strict).Object);
        var result = handler.OpenFolder(parsed);

        Assert.True(result);
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

        var handler = new FolderActionHandler(new Mock<INotificationService>(MockBehavior.Strict).Object);
        var result = handler.OpenFolder(parsed);

        Assert.False(result);
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

        var handler = new FolderActionHandler(new Mock<INotificationService>(MockBehavior.Strict).Object);
        var result = handler.OpenFolder(parsed);

        Assert.False(result);
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

        var handler = new FolderActionHandler(new Mock<INotificationService>(MockBehavior.Strict).Object);
        var result = handler.OpenFolder(parsed);

        Assert.False(result);
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

        var handler = new FolderActionHandler(new Mock<INotificationService>(MockBehavior.Strict).Object);
        var result = handler.OpenFolder(parsed);

        Assert.False(result);
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
