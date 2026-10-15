using Shoko.Companion.Launch;
using Xunit;

namespace Shoko.Companion.Tests;

public class UrlSchemeRegistrarTests
{
    [Fact]
    public void ResolveExecutablePath_UnderAnAppImage_RegistersTheAppImage()
    {
        var path = UrlSchemeRegistrar.ResolveExecutablePath("/home/me/Apps/shoko-companion.AppImage", "/tmp/.mount_shoko-abc123/usr/bin/shoko-companion");

        Assert.Equal("/home/me/Apps/shoko-companion.AppImage", path);
    }

    [Fact]
    public void ResolveExecutablePath_OutsideAnAppImage_RegistersTheProcess()
    {
        var path = UrlSchemeRegistrar.ResolveExecutablePath(null, "/usr/bin/shoko-companion");

        Assert.Equal("/usr/bin/shoko-companion", path);
    }
}
