using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ActorPlus.Web;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.Services;

/// <summary>
/// On server startup, tries to inject <script> and <link> tags into Jellyfin Web index.html.
/// This is a pragmatic approach used by a number of "custom javascript"-style plugins.
/// If the web folder is read-only, injection will fail gracefully.
/// </summary>
public sealed class IndexHtmlInjectorHostedService : IHostedService
{
    private const string StartMarker = "<!-- ActorPlus:BEGIN -->";
    private const string EndMarker = "<!-- ActorPlus:END -->";

    private readonly IApplicationPaths _paths;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IndexHtmlInjectorHostedService> _logger;

    public IndexHtmlInjectorHostedService(IApplicationPaths paths, IServiceProvider serviceProvider, ILogger<IndexHtmlInjectorHostedService> logger)
    {
        _paths = paths;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg?.InjectWebClientAssets != true)
            {
                return Task.CompletedTask;
            }

            // Preferred: in-memory transformation via jellyfin-plugin-file-transformation.
            // This avoids writing to /usr/share/jellyfin/web/index.html (fails when running Jellyfin as non-root).
            var transformationId = Plugin.Instance?.Id ?? Guid.Parse("d47f75a1-2c4c-4aa1-a1cd-5da6e5e6ccfd");
            if (FileTransformationIntegration.TryRegisterIndexHtmlTransformation(transformationId, _serviceProvider, _logger))
            {
                return Task.CompletedTask;
            }

            var webPath = _paths.WebPath;
            if (string.IsNullOrWhiteSpace(webPath))
            {
                _logger.LogWarning("WebPath is empty; cannot inject ActorPlus assets.");
                return Task.CompletedTask;
            }

            var indexPath = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogWarning("index.html not found at {IndexPath}; cannot inject ActorPlus assets.", indexPath);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexPath, Encoding.UTF8);
            if (html.Contains(StartMarker, StringComparison.Ordinal))
            {
                // already injected
                return Task.CompletedTask;
            }

            var insertTag = "</head>";
            var pos = html.IndexOf(insertTag, StringComparison.OrdinalIgnoreCase);
            if (pos < 0)
            {
                _logger.LogWarning("Could not locate </head> in index.html; cannot inject ActorPlus assets.");
                return Task.CompletedTask;
            }

            // IMPORTANT: use ../ because Jellyfin sets <base href=".../web/"> in index.
            // This keeps baseurl setups working (e.g., /jellyfin/web/ -> /jellyfin/ActorPlus/...)
            var injection = $@"
{StartMarker}
<link rel=""stylesheet"" type=""text/css"" href=""../ActorPlus/assets/birthage.css"" />
<script defer src=""../ActorPlus/assets/birthage.js""></script>
{EndMarker}
";

            var newHtml = html.Insert(pos, injection);

            // one-time backup
            var bakPath = indexPath + ".actorplus.bak";
            if (!File.Exists(bakPath))
            {
                File.WriteAllText(bakPath, html, Encoding.UTF8);
            }

            File.WriteAllText(indexPath, newHtml, Encoding.UTF8);
            _logger.LogInformation("Injected ActorPlus assets into {IndexPath}", indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject ActorPlus assets into Jellyfin Web index.html. The web folder may be read-only.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var transformationId = Plugin.Instance?.Id ?? Guid.Parse("d47f75a1-2c4c-4aa1-a1cd-5da6e5e6ccfd");
            FileTransformationIntegration.TryUnregisterIndexHtmlTransformation(transformationId, _serviceProvider, _logger);
        }
        catch
        {
            // non-fatal
        }

        return Task.CompletedTask;
    }
}
