using System;
using System.Diagnostics;
using System.IO;
using NLog;
using Shoko.Companion.Configuration;

#pragma warning disable CA1416
namespace Shoko.Companion.Launch;

/// <summary>
/// Registers and unregisters the <c>shoko://</c> URL scheme with the operating system.
/// </summary>
public static class UrlSchemeRegistrar
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const string Scheme = "shoko";

    /// <summary>
    /// Register the URL scheme handler with the OS.
    /// </summary>
    public static void Register()
    {
        if (OperatingSystem.IsWindows())
            RegisterWindows();
        else if (OperatingSystem.IsLinux())
            RegisterLinux();
        else if (OperatingSystem.IsMacOS())
            RegisterMac();
    }

    /// <summary>
    /// Unregister the URL scheme handler.
    /// </summary>
    public static void Unregister()
    {
        if (OperatingSystem.IsWindows())
            UnregisterWindows();
        else if (OperatingSystem.IsLinux())
            UnregisterLinux();
        else if (OperatingSystem.IsMacOS())
            UnregisterMac();
    }

    private static string ExecutablePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Cannot determine executable path");

    /// <summary>
    /// The quoted launcher portion of the registered command.
    /// Normally just the executable. In DEBUG, when the process is hosted by the
    /// <c>dotnet</c> muxer (i.e. running under <c>dotnet run</c> / an active debug
    /// session), this becomes <c>"dotnet" "&lt;entry.dll&gt;"</c> so the registered
    /// <c>shoko://</c> handler actually launches during the debug session instead of
    /// pointing at the bare muxer.
    /// </summary>
    private static string LaunchPrefix
    {
        get
        {
            var exe = ExecutablePath;
#if DEBUG
#pragma warning disable IL3000 // Assembly.Location is empty in single-file; this is DEBUG-only
            var entryDll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000
            var exeName = Path.GetFileNameWithoutExtension(exe);
            if (!string.IsNullOrEmpty(entryDll) &&
                exeName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Debug("Registering via the dotnet host launch format.");
                return $"\"{exe}\" \"{entryDll}\"";
            }
#endif
            return $"\"{exe}\"";
        }
    }

    /// <summary>
    /// When the current process uses a custom home (via <c>--home</c> or
    /// <c>SHOKO_COMPANION_HOME</c>), bake that home into the registered command so the
    /// OS-launched handler resolves the same config/home. Empty for default installs.
    /// </summary>
    private static string HomeArgument =>
        CompanionPaths.IsCustomHome ? $" --home \"{CompanionPaths.ConfigRoot}\"" : string.Empty;

    private static void RegisterWindows()
    {
        var key = $@"HKEY_CURRENT_USER\Software\Classes\{Scheme}";
        var command = $@"{LaunchPrefix}{HomeArgument} ""%1""";

        Microsoft.Win32.Registry.SetValue($@"{key}\shell\open\command", "", command);
        Microsoft.Win32.Registry.SetValue(key, "URL Protocol", "");
        Microsoft.Win32.Registry.SetValue($@"{key}\DefaultIcon", "", $@"""{ExecutablePath}"",1");

        Logger.Info($"Registered {Scheme}:// URL scheme on Windows.");
    }

    private static void UnregisterWindows()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Scheme}", throwOnMissingSubKey: false);
            Logger.Info($"Unregistered {Scheme}:// URL scheme on Windows.");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to unregister {Scheme} URL scheme on Windows", Scheme);
        }
    }

    private static void RegisterLinux()
    {
        var appsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications");
        Directory.CreateDirectory(appsDir);

        // Install the embedded app icon into the hicolor theme so `Icon=` resolves.
        AppIcon.InstallLinuxThemeIcon();

        var desktopFile = Path.Combine(appsDir, "shoko-url-handler.desktop");
        File.WriteAllText(desktopFile, $@"[Desktop Entry]
Type=Application
Name=Shoko Companion
Exec={LaunchPrefix}{HomeArgument} %u
Icon={AppIcon.ThemedName}
StartupNotify=false
MimeType=x-scheme-handler/{Scheme};
NoDisplay=true
");

        // Register with the MIME system (best-effort — tools may be absent).
        TryStart("xdg-mime", "default", "shoko-url-handler.desktop", $"x-scheme-handler/{Scheme}");
        TryStart("update-desktop-database", appsDir);

        var hicolorDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "icons", "hicolor");
        TryStart("gtk-update-icon-cache", "-f", "-t", hicolorDir);

        Logger.Info($"Registered {Scheme}:// URL scheme on Linux.");
    }

    private static void TryStart(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            // best-effort: the tool may not be installed
            Logger.Debug(ex, "Optional tool '{FileName}' failed or is not installed", fileName);
        }
    }

    private static void UnregisterLinux()
    {
        var desktopFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications", "shoko-url-handler.desktop");

        try
        {
            if (File.Exists(desktopFile))
                File.Delete(desktopFile);
            AppIcon.UninstallLinuxThemeIcon();
            Logger.Info($"Unregistered {Scheme}:// URL scheme on Linux.");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to unregister {Scheme} URL scheme on Linux", Scheme);
        }
    }

    private static void RegisterMac()
    {
        // On macOS, URL scheme registration requires an .app bundle with Info.plist.
        // This is best-effort — we register via the system's `defaults` command
        // for the current process, but it only takes effect within a proper bundle.
        Logger.Info($"Note: {Scheme}:// URL scheme on macOS requires an .app bundle with CFBundleURLTypes in Info.plist.");
        Logger.Info("See: https://developer.apple.com/documentation/bundleresources/information_property_list/cfbundleurltypes");
    }

    private static void UnregisterMac()
    {
        // No clean unregister on macOS outside of bundle removal
        Logger.Info($"To unregister {Scheme}:// on macOS, remove the associated .app bundle.");
    }
}
