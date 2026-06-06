using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;

namespace Shoko.Companion.Configuration;

/// <summary>
/// Access to the embedded application PNG icon, plus helpers to extract it to
/// locations the OS (notification daemons, desktop entries) can consume.
/// The PNG is embedded so it travels inside the single-file executable.
/// </summary>
public static class AppIcon
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Themed icon name installed into the XDG hicolor theme on Linux.
    /// </summary>
    public const string ThemedName = "shoko-companion";

    private const int IconSize = 128;

    /// <summary>
    /// Open the embedded icon PNG as a stream, or null if unavailable.
    /// </summary>
    public static Stream? OpenStream()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("icon.png", StringComparison.OrdinalIgnoreCase));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }

    /// <summary>
    /// Extract the embedded icon to a path. Returns the path, or null on failure.
    /// </summary>
    public static string? ExtractTo(string targetPath, bool overwrite = false)
    {
        try
        {
            if (!overwrite && File.Exists(targetPath))
                return targetPath;

            using var src = OpenStream();
            if (src is null)
                return null;

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var dst = File.Create(targetPath);
            src.CopyTo(dst);
            return targetPath;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to extract icon to {Path}", targetPath);
            return null;
        }
    }

    /// <summary>
    /// Stable per-user icon path inside the config root (notification fallback).
    /// </summary>
    public static string? EnsureConfigIcon()
    {
        if (!CompanionPaths.IsInitialized)
            return null;
        return ExtractTo(Path.Combine(CompanionPaths.ConfigRoot, "icon.png"));
    }

    /// <summary>
    /// Path of the icon when installed into the Linux hicolor theme.
    /// </summary>
    public static string LinuxThemeIconPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "icons", "hicolor", $"{IconSize}x{IconSize}", "apps", $"{ThemedName}.png");

    /// <summary>
    /// True if the themed icon has been installed (i.e. <c>register</c> was run).
    /// </summary>
    public static bool LinuxThemeIconInstalled => File.Exists(LinuxThemeIconPath);

    /// <summary>
    /// Install the icon into the Linux hicolor theme. Returns the path, or null.
    /// </summary>
    public static string? InstallLinuxThemeIcon() => ExtractTo(LinuxThemeIconPath, overwrite: true);

    /// <summary>
    /// Remove the themed icon from the Linux hicolor theme (best-effort).
    /// </summary>
    public static void UninstallLinuxThemeIcon()
    {
        try
        {
            if (File.Exists(LinuxThemeIconPath))
                File.Delete(LinuxThemeIconPath);
        }
        catch (Exception ex)
        {
            // best-effort
            Logger.Debug(ex, "Failed to remove themed icon {Path}", LinuxThemeIconPath);
        }
    }
}
