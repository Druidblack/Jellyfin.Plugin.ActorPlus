using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ActorPlus.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Enable rendering age badges on person images (web client).
    /// </summary>
    public bool EnableOverlay { get; set; } = true;

    /// <summary>
    /// Try to inject plugin JS/CSS into Jellyfin Web's index.html on server start.
    /// If Jellyfin web folder is read-only, injection will fail (overlay won't show) but API still works.
    /// </summary>
    public bool InjectWebClientAssets { get; set; } = true;

    /// <summary>
    /// Cache TTL in days for externally fetched birth/death dates.
    /// </summary>
    public int CacheTtlDays { get; set; } = 30;

    /// <summary>
    /// Use TMDB API as a fallback when Jellyfin person metadata doesn't include birthdate.
    /// Requires that the Person has a TMDB provider id.
    /// </summary>
    public bool UseTmdbFallback { get; set; } = false;

    /// <summary>
    /// TMDB API v3 key.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Show "age at death" instead of current age when death date is known.
    /// </summary>
    public bool ShowAgeAtDeath { get; set; } = true;

    /// <summary>
    /// On movie/series pages, also show person's age at the release date of the current title
    /// (computed from birth date and title premiere date).
    /// </summary>
    public bool ShowAgeAtRelease { get; set; } = true;

    /// <summary>
    /// When enabled, prepend icons to age labels: 🎂 for current age, 🎬 for age at release.
    /// </summary>
    public bool ShowAgeIcons { get; set; } = false;

    /// <summary>
    /// Show a birth country flag (Twemoji) in the bottom-left corner of person portraits.
    /// Requires that birthplace/country can be resolved from metadata.
    /// </summary>
    public bool ShowBirthCountryFlag { get; set; } = true;

    /// <summary>
    /// When enabled alongside the birth country flag, also show the birthplace text next to the flag.
    /// </summary>
    public bool ShowBirthPlaceText { get; set; } = false;

    /// <summary>
    /// If a person has a known death date, show a ✝ marker (top-left) and gray the portrait.
    /// </summary>

    public bool ShowDeceasedOverlay { get; set; } = false;

    /// <summary>
    /// When enabled, hovering a person portrait will show a small popup with filmography
    /// (library items where the person appears).
    /// </summary>
    public bool EnableHoverFilmography { get; set; } = false;

    /// <summary>
    /// Maximum number of filmography items to display in the hover popup.
    /// </summary>
    public int HoverFilmographyLimit { get; set; } = 12;

    /// <summary>
    /// When enabled, and filmography has more items than the configured limit, show a random subset on each hover.
    /// </summary>
    public bool RandomizeHoverFilmography { get; set; } = false;

    /// <summary>
    /// When enabled, hovering a movie/series poster will show a popup with the cast list (actors) for that title.
    /// </summary>
    public bool EnableHoverCastMenu { get; set; } = false;

    /// <summary>
    /// Maximum number of cast members (actors) to display in the hover popup for a movie/series poster.
    /// </summary>
    public int HoverCastLimit { get; set; } = 12;

}
