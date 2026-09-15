using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ActorPlus.Web;

/// <summary>
/// Callback methods invoked by File Transformation.
/// </summary>
public static class ActorPlusTransformationPatches
{
    /// <summary>
    /// Injects the Actor Plus CSS and JavaScript references into Jellyfin Web's index.html.
    /// </summary>
    /// <param name="payload">Current file contents supplied by File Transformation.</param>
    /// <returns>The transformed index.html contents.</returns>
    public static string IndexHtml(PatchRequestPayload payload)
    {
        return WebUiInjector.TransformIndexHtml(payload?.Contents ?? string.Empty);
    }
}

/// <summary>
/// Shape of the callback payload supplied by File Transformation.
/// This type intentionally lives in Actor Plus so the plugins do not share runtime model types
/// across separate AssemblyLoadContexts.
/// </summary>
public sealed class PatchRequestPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
