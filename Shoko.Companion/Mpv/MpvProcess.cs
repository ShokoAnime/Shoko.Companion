using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Shoko.Companion.Mpv;

/// <summary>
/// Helper for mpv binary discovery and launch.
/// </summary>
public static class MpvProcess
{
    /// <summary>
    /// Search PATH and common install locations for the mpv binary.
    /// Returns the full path to the first match, or null if not found.
    /// </summary>
    public static Task<string?> FindMpvAsync()
    {
        var candidates = new List<string>();

        // Search PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            var paths = pathEnv.Split(Path.PathSeparator);
            var exeName = OperatingSystem.IsWindows() ? "mpv.exe" : "mpv";
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var full = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(full))
                        candidates.Add(full);
                }
                catch
                {
                    // Ignore inaccessible directories
                }
            }
        }

        // Common install locations per platform
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(programFiles, "mpv", "mpv.exe"));

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (programFilesX86 != programFiles)
                candidates.Add(Path.Combine(programFilesX86, "mpv", "mpv.exe"));

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, "mpv", "mpv.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/usr/local/bin/mpv");
            candidates.Add("/opt/homebrew/bin/mpv");
            candidates.Add("/Applications/mpv.app/Contents/MacOS/mpv");
        }
        else
        {
            // Linux / Unix
            candidates.Add("/usr/bin/mpv");
            candidates.Add("/usr/local/bin/mpv");
            candidates.Add("/snap/bin/mpv");
            candidates.Add("/usr/bin/flatpak run io.mpv.Mpv");
        }

        // Return first existing file
        var found = candidates.FirstOrDefault(File.Exists);
        return Task.FromResult(found);
    }

    /// <summary>
    /// Return a platform-appropriate default IPC path for mpv.
    /// On Linux/Mac: /tmp/shoko-companion-mpv-{processId}.sock
    /// On Windows: \\.\pipe\shoko-companion-mpv-{processId}
    /// </summary>
    public static string GetDefaultIpcPath()
    {
        var pid = Environment.ProcessId;

        if (OperatingSystem.IsWindows())
            return $@"\\.\pipe\shoko-companion-mpv-{pid}";

        return $"/tmp/shoko-companion-mpv-{pid}.sock";
    }

    /// <summary>
    /// Launch mpv with JSON IPC enabled.
    /// </summary>
    /// <param name="mpvPath">Full path to the mpv binary.</param>
    /// <param name="ipcPath">IPC server path (Unix socket or named pipe).</param>
    /// <returns>The started Process, or null if launch failed.</returns>
    public static Process? Launch(string mpvPath, string ipcPath)
    {
        if (string.IsNullOrWhiteSpace(mpvPath) || !File.Exists(mpvPath))
            return null;

        var psi = new ProcessStartInfo(mpvPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
            ErrorDialog = false
        };

        psi.ArgumentList.Add($"--input-ipc-server={ipcPath}");
        psi.ArgumentList.Add("--no-terminal");
        psi.ArgumentList.Add("--keep-open");
        psi.ArgumentList.Add("--idle=yes");
        psi.ArgumentList.Add("--pause");

        try
        {
            var process = new Process { StartInfo = psi };
            process.EnableRaisingEvents = true;

            // Log stderr asynchronously (mpv logs go here)
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    System.Diagnostics.Trace.WriteLine($"[mpv stderr] {e.Data}");
            };

            if (!process.Start())
                return null;

            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            return null;
        }
    }
}
