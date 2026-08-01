using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shoko.Companion.Mpv;

/// <summary>
/// Controls an mpv media player instance via JSON IPC.
/// Provides methods for launching, connecting, file loading, property observation, and command execution.
/// </summary>
public interface IMpvController
{
    /// <summary>
    /// Raised when an observed mpv property changes value.
    /// </summary>
    event EventHandler<MpvPropertyChangeEventArgs>? PropertyChanged;

    /// <summary>
    /// Raised when mpv sends a generic event (e.g. file-loaded, end-file, shutdown).
    /// </summary>
    event EventHandler<MpvEventArgs>? MpvEvent;

    /// <summary>
    /// Raised when the connection to mpv is lost.
    /// </summary>
    event EventHandler? Disconnected;

    /// <summary>
    /// Gets whether the IPC connection to mpv is currently established.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets whether the mpv process is currently running.
    /// </summary>
    bool ProcessRunning { get; }

    /// <summary>
    /// Launch mpv with --input-ipc-server at the given path, then connect.
    /// </summary>
    Task<bool> LaunchAndConnectAsync(string mpvPath, string ipcPath, CancellationToken ct = default);

    /// <summary>
    /// Toggle mpv's video output. "gpu" shows the window, "null" hides it.
    /// </summary>
    Task SetVideoOutputAsync(string vo, CancellationToken ct = default);

    /// <summary>
    /// Connect to an already-running mpv instance at the given IPC path.
    /// </summary>
    Task ConnectAsync(string ipcPath, CancellationToken ct = default);

    /// <summary>
    /// Load a URL as the current file (replace current playlist).
    /// </summary>
    Task LoadFileAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Append a URL to the mpv playlist without interrupting current playback.
    /// </summary>
    Task AppendFileAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Append a URL as a playlist (e.g. an m3u8) without interrupting current
    /// playback. Uses <c>loadlist</c> so entries are parsed eagerly.
    /// </summary>
    Task AppendListAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Set an mpv property.
    /// </summary>
    Task SetPropertyAsync(string name, object value, CancellationToken ct = default);

    /// <summary>
    /// Get the value of an mpv property.
    /// </summary>
    Task<T?> GetPropertyAsync<T>(string name, CancellationToken ct = default);

    /// <summary>
    /// Start observing a property for changes.
    /// </summary>
    Task ObservePropertyAsync(int id, string name, CancellationToken ct = default);

    /// <summary>
    /// Stop observing a property.
    /// </summary>
    Task UnobservePropertyAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Send a command to mpv.
    /// </summary>
    Task SendCommandAsync(string command, object?[] args, CancellationToken ct = default);

    /// <summary>
    /// Disconnect and kill mpv if we launched it.
    /// </summary>
    Task StopAsync();
}
