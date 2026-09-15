using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ActorPlus.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.Services;

/// <summary>
/// Registers Actor Plus web transformations after the Jellyfin plugin host has been built.
/// </summary>
public sealed class FileTransformationRegistrationHostedService : IHostedService
{
    private static readonly Guid IndexHtmlTransformationId = Guid.Parse("cd3c40dc-2a2e-4ad7-bc0a-b9be6b6d3a08");
    private readonly ILogger<FileTransformationRegistrationHostedService> _logger;

    public FileTransformationRegistrationHostedService(ILogger<FileTransformationRegistrationHostedService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.InjectWebClientAssets != true)
        {
            _logger.LogInformation("ActorPlus: File Transformation web injection is disabled in plugin settings.");
            return Task.CompletedTask;
        }

        FileTransformationIntegration.TryRegisterIndexHtmlTransformation(IndexHtmlTransformationId, _logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // File Transformation registrations live for the Jellyfin process lifetime.
        // Jellyfin unloads plugins during shutdown, so no explicit unregister call is required.
        return Task.CompletedTask;
    }
}
