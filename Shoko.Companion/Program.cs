using System;
using System.Collections.Generic;
using Avalonia;
using Shoko.Companion.Launch;
using Shoko.Companion.Configuration;

namespace Shoko.Companion;

/// <summary>
/// Application entry-point. Parses CLI arguments, initialises paths and logging,
/// handles single-instance forwarding, and starts the Avalonia desktop app.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application entry-point.
    /// </summary>
    /// <param name="args">Command-line arguments (supports <c>--home</c>, <c>--force</c>,
    /// <c>register</c>, <c>unregister</c>, and <c>shoko://</c> URLs).</param>
    /// <returns>Exit code (0 for success).</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // Pull out --home before anything else so paths resolve to the right place.
        var homeOverride = ExtractHomeArg(ref args);
        var force = ExtractForceArg(ref args);

        CompanionPaths.Initialize(homeOverride);
        Logging.LogService.InitLogger();

        SingleInstanceManager.Initialize(force);

        if (SingleInstanceManager.AlreadyRunning)
        {
            if (ParseShokoUrl(args) is { } url)
                SingleInstanceManager.ForwardUrl(url);
            return 0;
        }

        // First instance: save the URL so the app can pick it up after startup
        if (ParseShokoUrl(args) is { } initialUrl)
            SingleInstanceManager.SetInitialUrl(initialUrl);

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Extract a <c>--home &lt;path&gt;</c> or <c>--home=&lt;path&gt;</c> argument, removing it
    /// (and its value) from <paramref name="args"/>. Returns the path, or null if absent.
    /// </summary>
    internal static string? ExtractHomeArg(ref string[] args)
    {
        string? home = null;
        var rest = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--home", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                    home = args[++i];
                continue;
            }
            if (arg.StartsWith("--home=", StringComparison.OrdinalIgnoreCase))
            {
                home = arg["--home=".Length..];
                continue;
            }
            rest.Add(arg);
        }

        args = rest.ToArray();
        return string.IsNullOrWhiteSpace(home) ? null : home;
    }

    /// <summary>
    /// Extract and remove all <c>--force</c> arguments from <paramref name="args"/>.
    /// </summary>
    internal static bool ExtractForceArg(ref string[] args)
    {
        var found = false;
        var rest = new List<string>(args.Length);

        foreach (var arg in args)
        {
            if (string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                continue;
            }
            rest.Add(arg);
        }

        args = rest.ToArray();
        return found;
    }

    internal static string? ParseShokoUrl(string[] args)
    {
        foreach (var arg in args)
        {
            // CLI verbs
            if (arg is "register") { UrlSchemeRegistrar.Register(); return null; }
            if (arg is "unregister") { UrlSchemeRegistrar.Unregister(); return null; }

            // shoko: URL, or a bare http(s) URL passed by an OS handler
            if (arg.StartsWith("shoko:", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return arg;
        }

        return null;
    }
}
