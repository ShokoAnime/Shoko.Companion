using System.Threading.Tasks;

namespace Shoko.Companion.Discord;

/// <summary>
/// Contract for managing Discord Rich Presence integration.
/// </summary>
public interface IDiscordPresenceService
{
    /// <summary>
    /// True once the Discord RPC client has connected and is ready to accept presence updates.
    /// </summary>
    public bool IsInitialized { get; }

    /// <summary>
    /// Initialize with the given client ID. No-op if already initialized.
    /// </summary>
    Task InitializeAsync(string clientId);

    /// <summary>
    /// Set or update the presence display.
    /// </summary>
    void SetPresence(DiscordPresenceData data);

    /// <summary>
    /// Show a generic idle presence (browsing Shoko).
    /// </summary>
    void SetIdlePresence();

    /// <summary>
    /// Clear presence entirely.
    /// </summary>
    void ClearPresence();

    /// <summary>
    /// Shut down the Discord connection.
    /// </summary>
    void Shutdown();
}
