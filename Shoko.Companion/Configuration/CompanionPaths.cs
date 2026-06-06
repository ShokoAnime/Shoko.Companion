using System;
using System.IO;

namespace Shoko.Companion.Configuration;

/// <summary>
/// Resolves and exposes key file-system paths used by the companion
/// (config root, settings file, logs directory).
/// </summary>
public static class CompanionPaths
{
    /// <summary>
    /// The resolved configuration root directory.
    /// </summary>
    public static string ConfigRoot { get; private set; } = null!;

    /// <summary>
    /// Full path to the settings.json file.
    /// </summary>
    public static string SettingsPath { get; private set; } = null!;

    /// <summary>
    /// Full path to the logs directory.
    /// </summary>
    public static string LogsPath { get; private set; } = null!;

    /// <summary>
    /// True once <see cref="Initialize"/> has been called and paths are ready.
    /// </summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>
    /// True when the config root came from an explicit <c>--home</c> argument or the
    /// <c>SHOKO_COMPANION_HOME</c> environment variable (i.e. not the platform default).
    /// </summary>
    public static bool IsCustomHome { get; private set; }

    /// <summary>
    /// Initializes all paths by resolving the config root directory.
    /// Creates directories if they do not already exist.
    /// </summary>
    /// <param name="homeOverride">Explicit home directory (from a <c>--home</c> argument).</param>
    public static void Initialize(string? homeOverride = null)
    {
        ConfigRoot = ResolveConfigRoot(homeOverride);
        Directory.CreateDirectory(ConfigRoot);

        SettingsPath = Path.Combine(ConfigRoot, "settings.json");
        LogsPath = Path.Combine(ConfigRoot, "logs");
        Directory.CreateDirectory(LogsPath);

        IsInitialized = true;

        if (IsCustomHome)
            Console.WriteLine($"Using custom home: {ConfigRoot}");
    }

    private static string ResolveConfigRoot(string? homeOverride)
    {
        // 1. Explicit --home argument (used by the registered URL handler).
        if (!string.IsNullOrWhiteSpace(homeOverride))
        {
            IsCustomHome = true;
            return Path.GetFullPath(homeOverride);
        }

        // 2. SHOKO_COMPANION_HOME environment variable (used during development).
        var envHome = Environment.GetEnvironmentVariable("SHOKO_COMPANION_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            IsCustomHome = true;
            return Path.GetFullPath(envHome);
        }

        // 3. Platform default.
        IsCustomHome = false;
        return GetPlatformDefaultRoot();
    }

    private static string GetPlatformDefaultRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "shoko-companion");
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "shoko-companion");
        }

        // Linux / Unix
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig))
            return Path.Combine(xdgConfig, "shoko-companion");

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".config", "shoko-companion");
    }
}
