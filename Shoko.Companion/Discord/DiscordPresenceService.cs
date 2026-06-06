using System;
using System.Linq;
using System.Threading.Tasks;
using DiscordRPC;
using NLog;

namespace Shoko.Companion.Discord;

/// <summary>
/// Manages Discord Rich Presence integration using Lachee's DiscordRichPresence library.
/// All public methods are safe to call when not initialized (no-ops).
/// Thread-safe for SetPresence calls from any thread.
/// </summary>
public class DiscordPresenceService : IDiscordPresenceService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly object _lock = new();

    private DiscordRpcClient? _client;
    private string? _currentClientId;
    private bool _isInitialized;

    /// <summary>
    /// True once the Discord RPC client has connected and is ready to accept presence updates.
    /// </summary>
    public bool IsInitialized
    {
        get
        {
            lock (_lock)
                return _isInitialized && _client?.IsDisposed is false;
        }
    }

    /// <summary>
    /// Initializes the Discord RPC connection with the specified client ID.
    /// No-op if already initialized with the same client ID.
    /// </summary>
    /// <param name="clientId">The Discord application client ID.</param>
    public async Task InitializeAsync(string clientId)
    {
        // No-op if already initialized with the same clientId
        lock (_lock)
        {
            if (_isInitialized && _client?.IsDisposed == false && _currentClientId == clientId)
            {
                Logger.Debug("Discord already initialized with client ID {ClientId}", clientId);
                return;
            }
        }

        // Deinitialize any existing client first
        Shutdown();

        Logger.Info("Initializing Discord Rich Presence with client ID {ClientId}", clientId);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new DiscordRpcClient(clientId, autoEvents: true);

        client.OnReady += (_, args) =>
        {
            Logger.Info(
                "Discord RPC connected (user: {Username})",
                args.User.Username);

            lock (_lock)
            {
                _isInitialized = true;
                _client = client;
                _currentClientId = clientId;
            }

            tcs.TrySetResult(true);
        };

        client.OnError += (_, args) =>
        {
            Logger.Error("Discord RPC error: {Message}", args.Message);

            lock (_lock)
            {
                if (_client == client)
                {
                    _isInitialized = false;
                    _currentClientId = null;
                    _client = null;
                }
            }

            tcs.TrySetException(new InvalidOperationException($"Discord RPC error: {args.Message}"));
        };

        client.OnClose += (_, args) =>
        {
            Logger.Warn("Discord RPC connection closed: {Reason}", args.Reason);

            lock (_lock)
            {
                if (_client == client)
                {
                    _isInitialized = false;
                    _currentClientId = null;
                }
            }
        };

        try
        {
            // Initialize() is synchronous/blocking (connects to Discord IPC);
            // run it on the thread pool to avoid blocking the caller.
            await Task.Run(() => client.Initialize());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to connect to Discord");
            client.Dispose();
            tcs.TrySetException(ex);
        }

        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Logger.Error("Discord initialization timed out after 10s — is the Discord desktop app running?");
            Shutdown();
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Discord initialization failed");
            Shutdown();
            throw;
        }
    }

    /// <summary>
    /// Sets the Discord Rich Presence to the given data.
    /// Safe to call when not initialized (no-op).
    /// </summary>
    /// <param name="data">The presence data to display.</param>
    public void SetPresence(DiscordPresenceData data)
    {
        DiscordRpcClient? client;

        lock (_lock)
        {
            if (!_isInitialized)
                return;

            client = _client;
        }

        if (client?.IsDisposed != false)
            return;

        var richPresence = new RichPresence
        {
            Type = ActivityType.Watching,
            Details = data.Details,
            State = data.State,
            Assets = new Assets
            {
                LargeImageKey = data.LargeImageKey ?? "shoko_default",
                LargeImageText = data.LargeImageText ?? "Shoko Server"
            },
            Timestamps = data.StartTimeStamp.HasValue
                ? new Timestamps { StartUnixMilliseconds = (ulong)data.StartTimeStamp.Value }
                : null,
            Buttons = data.Buttons?
                .Select(b => new Button { Label = b.Label, Url = b.Url })
                .ToArray()
        };

        client.SetPresence(richPresence);
    }

    /// <summary>
    /// Shows a generic idle presence indicating the user is browsing Shoko.
    /// Safe to call when not initialized (no-op).
    /// </summary>
    public void SetIdlePresence()
    {
        var data = new DiscordPresenceData(
            Details: "Shoko Companion",
            State: "Browsing",
            LargeImageKey: null,
            LargeImageText: null,
            StartTimeStamp: null
        );
        SetPresence(data);
    }

    /// <summary>
    /// Clears the current Discord Rich Presence display.
    /// Safe to call when not initialized (no-op).
    /// </summary>
    public void ClearPresence()
    {
        DiscordRpcClient? client;

        lock (_lock)
        {
            if (!_isInitialized)
                return;

            client = _client;
        }

        if (client?.IsDisposed != false)
            return;

        client.ClearPresence();
        client.SetPresence(null);
    }

    /// <summary>
    /// Shuts down the Discord RPC connection, clearing presence and disposing resources.
    /// Safe to call multiple times.
    /// </summary>
    public void Shutdown()
    {
        DiscordRpcClient? oldClient;

        lock (_lock)
        {
            oldClient = _client;
            _client = null;
            _isInitialized = false;
            _currentClientId = null;
        }

        if (oldClient is not null && !oldClient.IsDisposed)
        {
            Logger.Info("Shutting down Discord RPC");

            try
            {
                oldClient.ClearPresence();
                oldClient.Deinitialize();
                oldClient.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Error during Discord RPC shutdown");
            }
        }
    }
}
