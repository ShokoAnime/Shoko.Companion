namespace Shoko.Companion.Discord;

/// <summary>
/// Sources for Discord Rich Presence buttons.
/// Each maps to a known external database for the current item.
/// </summary>
public enum DiscordButtonSource
{
    /// <summary>No button (disabled).</summary>
    Disabled,

    /// <summary>Link to AniDB using the anime/series ID.</summary>
    AniDB,

    /// <summary>Link to TMDB using the show or movie ID.</summary>
    TMDB,

    /// <summary>Link to TVDB (for series) or IMDb (for movies). Prefers IMDb when both exist.</summary>
    TvdbOrImdb,
}
