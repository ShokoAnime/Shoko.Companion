using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Shoko.Companion.Mpv;

/// <summary>
/// Hand-rolled mpv JSON IPC client using a Unix domain socket (Linux/macOS)
/// or a named pipe (Windows).
/// </summary>
public class MpvIpcClient : IMpvController, IAsyncDisposable
{
    #region Events

    /// <summary>
    /// Raised when an observed mpv property changes value.
    /// </summary>
    public event EventHandler<MpvPropertyChangeEventArgs>? PropertyChanged;

    /// <summary>
    /// Raised when mpv sends a generic event (e.g. file-loaded, end-file, shutdown).
    /// </summary>
    public event EventHandler<MpvEventArgs>? MpvEvent;

    /// <summary>
    /// Raised when the connection to mpv is lost.
    /// </summary>
    public event EventHandler? Disconnected;

    #endregion

    #region Properties

    /// <summary>
    /// Gets whether the IPC connection to mpv is currently established.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Gets whether the mpv process is currently running.
    /// </summary>
    public bool ProcessRunning { get; private set; }

    #endregion

    #region Fields

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Process? _process;
    private Stream? _stream;
    private StreamReader? _reader;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private int _nextRequestId;
    private bool _weLaunchedMpv;
    private string? _ipcPath;
    private bool _disposed;

    private const int DefaultResponseTimeoutMs = 30_000;

    #endregion

    #region Connect / Disconnect

    /// <summary>
    /// Launch mpv with --input-ipc-server at the given path, then connect.
    /// </summary>
    public async Task<bool> LaunchAndConnectAsync(string mpvPath, string ipcPath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Kill any previously launched process
        if (_process is not null)
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            _process.Dispose();
            _process = null;
        }

        _weLaunchedMpv = true;
        _ipcPath = ipcPath;

        // Delete any stale socket file from a previous mpv instance that may have crashed,
        // so the new mpv process can bind to the same path without conflict.
        DeleteIpcSocketIfExists(ipcPath);

        Logger.Info("Launching mpv: {MpvPath} with IPC at {IpcPath}", mpvPath, ipcPath);

        _process = MpvProcess.Launch(mpvPath, ipcPath);
        if (_process is null)
        {
            Logger.Error("Failed to launch mpv at {MpvPath}", mpvPath);
            _weLaunchedMpv = false;
            return false;
        }

        ProcessRunning = true;

        // Hook process exit
        _process.Exited += OnProcessExited;

        // Wait for the IPC endpoint to exist
        if (OperatingSystem.IsWindows())
        {
            // On Windows, NamedPipeClientStream.ConnectAsync with timeout handles this
            await Task.Delay(500, ct);
        }
        else
        {
            // On Unix, poll for the socket file to appear
            var timeout = TimeSpan.FromSeconds(15);
            var sw = Stopwatch.StartNew();
            while (!File.Exists(ipcPath) && sw.Elapsed < timeout)
            {
                await Task.Delay(100, ct);
            }

            if (!File.Exists(ipcPath))
            {
                Logger.Error("mpv IPC socket {IpcPath} did not appear within {Timeout}s", ipcPath, timeout.TotalSeconds);
                ProcessRunning = false;
                _weLaunchedMpv = false;
                return false;
            }
        }

        await ConnectAsync(ipcPath, ct);
        return IsConnected;
    }

    /// <summary>
    /// Connect to an already-running mpv instance at the given IPC path.
    /// </summary>
    public async Task ConnectAsync(string ipcPath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Disconnect any existing connection
        await DisconnectAsync();

        _ipcPath = ipcPath;
        Logger.Info("Connecting to mpv IPC at {IpcPath}", ipcPath);

        Stream stream;
        if (OperatingSystem.IsWindows())
        {
            var pipeName = ipcPath;
            const string winPipePrefix = @"\\.\pipe\";
            if (pipeName.StartsWith(winPipePrefix, StringComparison.OrdinalIgnoreCase))
                pipeName = pipeName[winPipePrefix.Length..];

            var pipeStream = new NamedPipeClientStream(
                serverName: ".",
                pipeName: pipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);

            await pipeStream.ConnectAsync(ct);
            stream = pipeStream;
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(ipcPath), ct);
            stream = new NetworkStream(socket, ownsSocket: true);
        }

        _stream = stream;
        _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        IsConnected = true;

        Logger.Info("Connected to mpv IPC");

        // Start the background read loop
        _readLoopCts = new CancellationTokenSource();
        _readLoopTask = ReadLoopAsync(_readLoopCts.Token);
    }

    private async Task DisconnectAsync()
    {
        _readLoopCts?.Cancel();

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask; }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { Logger.Debug(ex, "Read loop task exited during disconnect"); }
        }

        _readLoopTask = null;
        _readLoopCts?.Dispose();
        _readLoopCts = null;

        if (_reader is not null)
        {
            try { _reader.Dispose(); }
            catch { /* ignore */ }
            _reader = null;
        }

        if (_stream is not null)
        {
            try { _stream.Dispose(); }
            catch { /* ignore */ }
            _stream = null;
        }

        IsConnected = false;
    }

    #endregion

    #region Commands

    /// <summary>
    /// Load a URL as the current file (replace current playlist).
    /// </summary>
    public async Task LoadFileAsync(string url, CancellationToken ct = default)
    {
        await SendCommandAndCheckAsync("loadfile", [url, "replace"], ct);
    }

    /// <summary>
    /// Append a URL to the mpv playlist without interrupting current playback.
    /// </summary>
    public async Task AppendFileAsync(string url, CancellationToken ct = default)
    {
        await SendCommandAndCheckAsync("loadfile", [url, "append"], ct);
    }

    /// <summary>
    /// Set an mpv property.
    /// </summary>
    public async Task SetPropertyAsync(string name, object value, CancellationToken ct = default)
    {
        await SendCommandAndCheckAsync("set_property", [name, value], ct);
    }

    /// <summary>
    /// Get the value of an mpv property.
    /// </summary>
    public async Task<T?> GetPropertyAsync<T>(string name, CancellationToken ct = default)
    {
        var response = await SendCommandAndGetResponseAsync("get_property", [name], ct);
        var data = response["data"];
        if (data is null || data.Type == JTokenType.Null)
            return default;

        return data.ToObject<T>();
    }

    /// <summary>
    /// Start observing a property for changes.
    /// </summary>
    public async Task ObservePropertyAsync(int id, string name, CancellationToken ct = default)
    {
        await SendCommandAndCheckAsync("observe_property", [id, name], ct);
    }

    /// <summary>
    /// Stop observing a property.
    /// </summary>
    public async Task UnobservePropertyAsync(int id, CancellationToken ct = default)
    {
        await SendCommandAndCheckAsync("unobserve_property", [id], ct);
    }

    /// <summary>
    /// Send a command to mpv.
    /// </summary>
    public async Task SendCommandAsync(string command, object?[] args, CancellationToken ct = default)
    {
        await SendCommandAndCheckAsync(command, args, ct);
    }

    /// <summary>
    /// Switch video output driver. "null" hides the window, "gpu" shows it.
    /// </summary>
    public async Task SetVideoOutputAsync(string vo, CancellationToken ct = default)
    {
        await SendCommandAndCheckAsync("set", ["vo", vo], ct);
    }

    #endregion

    #region Stop

    /// <summary>
    /// Disconnect and kill mpv if we launched it.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed)
            return;

        // Try to quit mpv gracefully
        if (IsConnected)
        {
            try
            {
                // Send quit with short timeout — mpv shuts down IPC before responding
                using var quitCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendCommandAsync("quit", [], quitCts.Token);
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                // Send/response failures during shutdown are expected — mpv closes the
                // IPC channel before acknowledging the quit command.
                Logger.Debug(ex, "Ignoring mpv quit send/response failure during shutdown");
            }
        }

        await DisconnectAsync();

        // Kill the process if we launched it and it's still running
        if (_process is not null && _weLaunchedMpv)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Error killing mpv process during stop");
            }

            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }

        // Clean up Unix domain socket file
        DeleteIpcSocketIfExists(_ipcPath);

        ProcessRunning = false;
        _weLaunchedMpv = false;
    }

    #endregion

    #region Read Loop

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Debug.Assert(_reader is not null);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(ct);

                // null means end of stream
                if (line is null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ProcessIncomingMessage(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "mpv read loop error");
        }

        // Read loop has exited — connection is lost
        await HandleDisconnect();
    }

    private void ProcessIncomingMessage(string line)
    {
        Logger.Trace("mpv << {Line}", line);

        try
        {
            var msg = JObject.Parse(line);

            // Check if this is a command response (has request_id)
            if (msg.TryGetValue("request_id", out var ridToken) && ridToken.Type == JTokenType.Integer)
            {
                var requestId = ridToken.Value<int>();
                if (_pendingRequests.TryRemove(requestId, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }
                else
                {
                    Logger.Warn("Received response for unknown request_id {RequestId}", requestId);
                }
                return;
            }

            // Otherwise it's an event
            if (msg.TryGetValue("event", out var eventToken))
            {
                var eventName = eventToken.Value<string>() ?? string.Empty;
                var eventData = msg["data"];

                // Fire generic event
                MpvEvent?.Invoke(this, new MpvEventArgs(eventName, eventData));

                // Fire typed property change event
                if (eventName == "property-change" && eventData is not null)
                {
                    var propId = msg["id"]?.Value<int>();
                    var propName = msg["name"]?.Value<string>();
                    if (propName is not null)
                    {
                        PropertyChanged?.Invoke(this, new MpvPropertyChangeEventArgs(
                            propId ?? 0, propName, eventData?.ToObject<object>()));
                    }
                }

                // On shutdown, flag the connection as ending
                if (eventName == "shutdown")
                {
                    Logger.Info("mpv sent shutdown event");
                }
            }
        }
        catch (JsonReaderException ex)
        {
            Logger.Warn(ex, "Failed to parse mpv message: {Line}", line);
        }
    }

    private async Task HandleDisconnect()
    {
        if (!IsConnected)
            return;

        IsConnected = false;
        Logger.Info("mpv IPC disconnected");

        // Fail all pending requests
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetException(new InvalidOperationException("mpv disconnected"));
        }
        _pendingRequests.Clear();

        // Clean up stream resources
        if (_reader is not null)
        {
            try { _reader.Dispose(); }
            catch { /* ignore */ }
            _reader = null;
        }

        if (_stream is not null)
        {
            try { _stream.Dispose(); }
            catch { /* ignore */ }
            _stream = null;
        }

        // Notify listeners
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Internal Helpers

    private async Task<JObject> SendCommandAndGetResponseAsync(string command, object?[]? args, CancellationToken ct)
    {
        EnsureNotDisposed();

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(requestId, tcs))
        {
            // This should never happen with Interlocked.Increment
            throw new InvalidOperationException("Failed to register pending request");
        }

        try
        {
            await SendRawMessageAsync(command, args, requestId, ct);

            // Wait for response with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultResponseTimeoutMs);

            var response = await tcs.Task.WaitAsync(timeoutCts.Token);

            // Check for errors
            var error = response["error"]?.Value<string>();
            if (error is not null && error != "success")
            {
                var errorString = response["error_string"]?.Value<string>();
                var message = errorString is not null
                    ? $"mpv command '{command}' failed: {error} ({errorString})"
                    : $"mpv command '{command}' failed: {error}";
                throw new InvalidOperationException(message);
            }

            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw new TimeoutException($"mpv command '{command}' timed out after {DefaultResponseTimeoutMs}ms");
        }
        catch (Exception)
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    private async Task SendCommandAndCheckAsync(string command, object?[]? args, CancellationToken ct)
    {
        await SendCommandAndGetResponseAsync(command, args, ct);
    }

    private async Task SendRawMessageAsync(string command, object?[]? args, int requestId, CancellationToken ct)
    {
        EnsureConnected();

        // Build command array: [command, ...args]
        var cmdArray = new object?[1 + (args?.Length ?? 0)];
        cmdArray[0] = command;
        if (args is { Length: > 0 })
            Array.Copy(args, 0, cmdArray, 1, args.Length);

        var payload = new Dictionary<string, object?>
        {
            ["command"] = cmdArray,
            ["request_id"] = requestId
        };

        var json = JsonConvert.SerializeObject(payload, Formatting.None);
        var data = Encoding.UTF8.GetBytes(json + "\n");

        await _writeLock.WaitAsync(ct);
        try
        {
            EnsureConnected();
            await _stream!.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);

            Logger.Trace("Sent mpv command [{RequestId}]: {Command}", requestId, json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Best-effort deletion of the mpv IPC Unix domain socket file. No-op on Windows,
    /// where the IPC endpoint is a named pipe with no filesystem artifact. Failures are
    /// swallowed and logged at debug level.
    /// </summary>
    private static void DeleteIpcSocketIfExists(string? ipcPath)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(ipcPath) || !File.Exists(ipcPath))
            return;

        try
        {
            File.Delete(ipcPath);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not delete IPC socket {IpcPath}", ipcPath);
        }
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        ProcessRunning = false;
        Logger.Info("mpv process exited");

        // Clean up the stale Unix domain socket file so a future launch can bind to the same path.
        DeleteIpcSocketIfExists(_ipcPath);

        // If we're still connected, the read loop will detect the closed stream
        // and fire Disconnected. But if the stream was already cleaned up on Unix
        // (socket file deleted), the read loop might not detect it immediately.
        // Trigger disconnect proactively.
        if (IsConnected)
        {
            // Fire disconnect on a background task to avoid deadlock from the Exited event handler
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleDisconnect();
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Error during disconnect triggered by process exit");
                }
            });
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConnected || _stream is null || _reader is null)
            throw new InvalidOperationException("Not connected to mpv");
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Releases all resources used by the mpv IPC client.
    /// Stops playback, disposes the write lock, and suppresses finalization.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync();
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}
