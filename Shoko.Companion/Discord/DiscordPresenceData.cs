using System.Collections.Generic;

namespace Shoko.Companion.Discord;

/// <summary>
/// Data for setting Discord Rich Presence state.
/// </summary>
/// <param name="Details">The first line of the presence (e.g. episode title).</param>
/// <param name="State">The second line of the presence (e.g. series name).</param>
/// <param name="LargeImageKey">Key for the large image asset registered on Discord.</param>
/// <param name="LargeImageText">Tooltip text for the large image.</param>
/// <param name="StartTimeStamp">Unix timestamp (ms) when playback started, for elapsed-time display.</param>
/// <param name="Buttons">Optional buttons shown in the presence.</param>
public record DiscordPresenceData(
    string? Details,
    string? State,
    string? LargeImageKey,
    string? LargeImageText,
    long? StartTimeStamp,
    IReadOnlyList<DiscordButtonData>? Buttons = null
);
