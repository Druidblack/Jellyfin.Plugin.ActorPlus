using System;

namespace Jellyfin.Plugin.ActorPlus.Web;

/// <summary>
/// HTML transformer that injects Actor Plus (ActorPlus) web assets into Jellyfin Web's <c>index.html</c>.
/// </summary>
internal static class WebUiInjector
{
    private const string StartMarker = "<!-- ActorPlus:BEGIN -->";
    private const string EndMarker = "<!-- ActorPlus:END -->";

    public static string TransformIndexHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        // Already injected.
        if (html.Contains(StartMarker, StringComparison.Ordinal))
        {
            return html;
        }

        var insertTag = "</head>";
        var pos = html.IndexOf(insertTag, StringComparison.OrdinalIgnoreCase);
        if (pos < 0)
        {
            return html;
        }

        // IMPORTANT: use ../ because Jellyfin sets <base href=".../web/"> in index.
        // This keeps baseurl setups working (e.g., /jellyfin/web/ -> /jellyfin/ActorPlus/...).
        var injection = $@"
{StartMarker}
<link rel=""stylesheet"" type=""text/css"" href=""../ActorPlus/assets/birthage.css"" />
<script defer src=""../ActorPlus/assets/birthage.js""></script>
{EndMarker}
";

        return html.Insert(pos, injection);
    }
}
