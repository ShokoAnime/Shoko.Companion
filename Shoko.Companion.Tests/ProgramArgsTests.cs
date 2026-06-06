using Xunit;

namespace Shoko.Companion.Tests;

public class ProgramArgsTests
{
    [Fact]
    public void ExtractHomeArg_SpaceForm_RemovesAndReturns()
    {
        var args = new[] { "--home", "/tmp/x", "shoko:http://h/p?playlist=s1" };
        var home = Program.ExtractHomeArg(ref args);

        Assert.Equal("/tmp/x", home);
        Assert.Equal(new[] { "shoko:http://h/p?playlist=s1" }, args);
    }

    [Fact]
    public void ExtractHomeArg_EqualsForm_RemovesAndReturns()
    {
        var args = new[] { "--home=/tmp/y", "shoko:http://h" };
        var home = Program.ExtractHomeArg(ref args);

        Assert.Equal("/tmp/y", home);
        Assert.Equal(new[] { "shoko:http://h" }, args);
    }

    [Fact]
    public void ExtractHomeArg_Absent_ReturnsNull()
    {
        var args = new[] { "shoko:http://h" };
        var home = Program.ExtractHomeArg(ref args);

        Assert.Null(home);
        Assert.Single(args);
    }

    [Fact]
    public void ParseShokoUrl_FindsUrl_EvenWhenHomePrecedesIt()
    {
        // Simulates the OS launching: <exe> --home <path> shoko:<url>
        var args = new[] { "--home", "/tmp/x", "shoko:http://h/p" };
        Program.ExtractHomeArg(ref args);

        Assert.Equal("shoko:http://h/p", Program.ParseShokoUrl(args));
    }
}
