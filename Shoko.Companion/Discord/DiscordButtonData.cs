namespace Shoko.Companion.Discord;

/// <summary>
/// Represents a clickable button displayed in the Discord Rich Presence.
/// </summary>
/// <param name="Label">The button label text.</param>
/// <param name="Url">The URL the button opens.</param>
public record DiscordButtonData(string Label, string Url);
