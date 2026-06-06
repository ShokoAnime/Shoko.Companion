using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using NLog;
using Shoko.Companion.Configuration;

namespace Shoko.Companion.Launch;

/// <summary>
/// Ensures only one instance of the companion runs per data directory.
/// Uses a PID lock file instead of a named Mutex (which is unreliable on Linux).
/// </summary>
public static class SingleInstanceManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const string LockFileName = ".lockfile";
    private const string PipeName = "Shoko.Companion.UrlPipe";

    /// <summary>
    /// true if another instance is already running.
    /// </summary>
    public static bool AlreadyRunning { get; private set; }

    /// <summary>
    /// Fires when a URL is forwarded from a secondary instance to this running primary instance.
    /// Subscribe once during app startup (e.g. in <c>OnFrameworkInitializationCompleted</c>).
    /// </summary>
    public static event Action<string>? UrlReceived;

    /// <summary>
    /// URL received from a secondary instance via the forwarding pipe.
    /// </summary>
    private static string? _pendingUrl;

    private static string? _lockFilePath;

    /// <param name="force">
    /// When true, skip the running-instance check and overwrite the lock file.
    /// </param>
    public static void Initialize(bool force = false)
    {
        _lockFilePath = Path.Combine(CompanionPaths.ConfigRoot, LockFileName);

        if (!TryAcquireLock(force))
        {
            AlreadyRunning = true;
            return;
        }

        AlreadyRunning = false;

        // Best-effort cleanup on normal exit.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();

        // Start the pipe server to receive forwarded URLs from secondary instances
        Task.Run(ListenForUrls);
    }

    /// <summary>
    /// Release the lock file if it is still owned by the current process.
    /// </summary>
    public static void Release()
    {
        if (_lockFilePath is null || !File.Exists(_lockFilePath))
            return;

        try
        {
            var text = File.ReadAllText(_lockFilePath, Encoding.UTF8).Trim();
            if (text == Environment.ProcessId.ToString())
                File.Delete(_lockFilePath);
        }
        catch
        {
            // Best-effort — ignore cleanup errors.
        }
    }

    /// <summary>
    /// Forward a URL to the primary instance.
    /// </summary>
    public static void ForwardUrl(string url)
    {
        Logger.Info("Forwarding URL to primary instance: {Url}", url);
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            using var writer = new StreamWriter(client);
            writer.Write(url);
            writer.Flush();
            Logger.Info("URL forwarded successfully");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to forward URL to primary instance");
        }
    }

    /// <summary>
    /// Set the initial URL from CLI arguments (called before the app starts
    /// when this is the first/primary instance). The app picks it up later
    /// via <see cref="ConsumePendingUrl"/>.
    /// </summary>
    internal static void SetInitialUrl(string url)
    {
        _pendingUrl = url;
        Logger.Info("Stored initial URL from CLI: {Url}", url);
    }

    /// <summary>
    /// Consume a forwarded URL (called from the primary instance after startup).
    /// </summary>
    public static string? ConsumePendingUrl()
    {
        var url = _pendingUrl;
        _pendingUrl = null;
        return url;
    }

    private static bool TryAcquireLock(bool force)
    {
        if (!force && File.Exists(_lockFilePath))
        {
            try
            {
                var text = File.ReadAllText(_lockFilePath, Encoding.UTF8).Trim();
                if (int.TryParse(text, out var pid))
                {
                    try
                    {
                        _ = Process.GetProcessById(pid);
                        // Process exists — another instance is alive.
                        Logger.Warn("Another instance is already running (PID: {Pid}). Use --force to override.", pid);
                        return false;
                    }
                    catch (ArgumentException)
                    {
                        // Process doesn't exist — stale lock, safe to overwrite.
                        Logger.Info("Removing stale lock from PID {Pid}.", pid);
                    }
                }
                else
                {
                    Logger.Info("Ignoring invalid lock file contents.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to read lock file. Use --force to override.");
                return false;
            }
        }

        // Write our PID to the lock file.
        try
        {
            var dir = Path.GetDirectoryName(_lockFilePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(_lockFilePath!, Environment.ProcessId.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to write lock file");
            return false;
        }

        return true;
    }

    private static async Task ListenForUrls()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var url = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    Logger.Info("Received forwarded URL: {Url}", url);
                    _pendingUrl = url;
                    UrlReceived?.Invoke(url);
                }
                else
                {
                    Logger.Warn("Received empty forwarded URL");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Pipe server error, retrying in 500ms");
                await Task.Delay(500);
            }
        }
    }
}
