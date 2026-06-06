using Shoko.Companion.Server.Models;
using Xunit;

namespace Shoko.Companion.Tests;

public class ScrobbleEventTypeTests
{
    [Theory]
    [InlineData(ScrobbleEventType.PlaybackStart, "play")]
    [InlineData(ScrobbleEventType.PlaybackEnd, "stop")]
    [InlineData(ScrobbleEventType.PlaybackPause, "pause")]
    [InlineData(ScrobbleEventType.PlaybackResume, "resume")]
    [InlineData(ScrobbleEventType.PlaybackProgress, "scrobble")]
    [InlineData(ScrobbleEventType.None, "user-interaction")]
    [InlineData(ScrobbleEventType.UserInteraction, "user-interaction")]
    [InlineData(ScrobbleEventType.Import, "user-interaction")]
    public void ToQueryValue_ReturnsExpected(ScrobbleEventType type, string expected)
    {
        var result = type.ToQueryValue();
        Assert.Equal(expected, result);
    }
}
