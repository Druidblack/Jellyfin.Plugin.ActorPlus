using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.ScheduledTasks;

/// <summary>
/// Scheduled task: update people (actor) primary images by picking the remote image with the highest resolution.
/// </summary>
public sealed class UpdatePersonImagesByResolutionTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<UpdatePersonImagesByResolutionTask> _logger;

    public UpdatePersonImagesByResolutionTask(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ILogger<UpdatePersonImagesByResolutionTask> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    public string Name => "ActorPlus: Update the actors' photos to the maximum resolution";

    public string Key => "ActorPlus_UpdatePersonImagesByResolution";

    public string Description => "For all actors, selects the deleted Primary photo with the maximum resolution and saves it as the main one.";

    public string Category => "ActorPlus";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Query all people items.
        IReadOnlyList<BaseItem> people;
        try
        {
            people = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Person },
                Recursive = true,
                Limit = int.MaxValue
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query people items");
            return;
        }

        if (people.Count == 0)
        {
            _logger.LogInformation("No people items found");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Starting person image refresh by resolution. People: {Count}", people.Count);

        var processed = 0;
        var updated = 0;
        var skippedNoRemote = 0;

        foreach (var baseItem in people)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            // Safety: only operate on Person items.
            if (baseItem is not Person person)
            {
                progress.Report(processed * 100d / people.Count);
                continue;
            }

            IReadOnlyList<RemoteImageInfo> remoteImages;
            try
            {
                var query = new RemoteImageQuery(string.Empty)
                {
                    IncludeAllLanguages = true,
                    IncludeDisabledProviders = true,
                    ImageType = ImageType.Primary
                };

                // Jellyfin ProviderManager API (10.11+) uses the 3-arg overload.
                remoteImages = (await _providerManager.GetAvailableRemoteImages(person, query, cancellationToken)
                    .ConfigureAwait(false)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get remote images for person {Name} ({Id})", person.Name, person.Id);
                progress.Report(processed * 100d / people.Count);
                continue;
            }

            var best = PickBestByResolution(remoteImages);
            if (best is null || string.IsNullOrWhiteSpace(best.Url))
            {
                skippedNoRemote++;
                progress.Report(processed * 100d / people.Count);
                continue;
            }

            try
            {
                // Save the selected remote image as Primary (index 0) to REPLACE the current primary
                // and force tag/image-metadata updates.
                await _providerManager.SaveImage(person, best.Url, ImageType.Primary, 0, cancellationToken)
                    .ConfigureAwait(false);

                // Persist + notify so web clients immediately see the new image tag (no manual "Edit image" needed).
                await TryPersistAndNotifyImageUpdateAsync(person, cancellationToken).ConfigureAwait(false);

                updated++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save best image for person {Name} ({Id})", person.Name, person.Id);
            }

            progress.Report(processed * 100d / people.Count);
        }

        _logger.LogInformation(
            "Person image refresh finished. Processed={Processed}, Updated={Updated}, NoRemote={NoRemote}",
            processed,
            updated,
            skippedNoRemote);

        progress.Report(100);
    }

    private static RemoteImageInfo? PickBestByResolution(IReadOnlyList<RemoteImageInfo> images)
    {
        if (images.Count == 0)
        {
            return null;
        }

        // Prefer items with known width/height.
        // If providers don't supply dimensions, we skip to avoid accidentally replacing a good image.
        return images
            .Where(i => !string.IsNullOrWhiteSpace(i.Url))
            .Select(i => new { Img = i, Area = (long)(i.Width ?? 0) * (long)(i.Height ?? 0) })
            .Where(x => x.Area > 0)
            .OrderByDescending(x => x.Area)
            .Select(x => x.Img)
            .FirstOrDefault();
    }

    private async Task TryPersistAndNotifyImageUpdateAsync(Person person, CancellationToken cancellationToken)
    {
        try
        {
            // 1) Persist the item (some Jellyfin builds update ImageTags only after an item save).
            // Use reflection to avoid tight coupling to internal signatures.
            var personObj = (object)person;
            var t = personObj.GetType();

            // Common signature: UpdateToRepositoryAsync(ItemUpdateType updateType, CancellationToken ct)
            var m1 = t.GetMethod("UpdateToRepositoryAsync", new[] { typeof(ItemUpdateType), typeof(CancellationToken) });
            if (m1 is not null)
            {
                var task = (Task?)m1.Invoke(personObj, new object[] { ItemUpdateType.ImageUpdate, cancellationToken });
                if (task is not null) await task.ConfigureAwait(false);
            }
            else
            {
                // Alternate signature: UpdateToRepositoryAsync(CancellationToken ct)
                var m2 = t.GetMethod("UpdateToRepositoryAsync", new[] { typeof(CancellationToken) });
                if (m2 is not null)
                {
                    var task = (Task?)m2.Invoke(personObj, new object[] { cancellationToken });
                    if (task is not null) await task.ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Non-fatal: failed to persist person {Name} after image save", person.Name);
        }

        try
        {
            // 2) Notify library manager about image update so connected clients refresh their cached image tags.
            // Reflection again: signatures differ across Jellyfin versions.
            var lmObj = (object)_libraryManager;
            var lmType = lmObj.GetType();

            // Try: UpdateItem(BaseItem item, BaseItem? parent, ItemUpdateType updateType, bool? notify)
            // or variants without parent/notify.
            var methods = lmType.GetMethods().Where(m => m.Name == "UpdateItem").ToArray();
            foreach (var m in methods)
            {
                var p = m.GetParameters();
                try
                {
                    if (p.Length == 3 && p[0].ParameterType.IsAssignableFrom(typeof(BaseItem)) && p[2].ParameterType == typeof(ItemUpdateType))
                    {
                        m.Invoke(lmObj, new object?[] { person, null, ItemUpdateType.ImageUpdate });
                        return;
                    }

                    if (p.Length == 2 && p[0].ParameterType.IsAssignableFrom(typeof(BaseItem)) && p[1].ParameterType == typeof(ItemUpdateType))
                    {
                        m.Invoke(lmObj, new object?[] { person, ItemUpdateType.ImageUpdate });
                        return;
                    }

                    if (p.Length == 4 && p[0].ParameterType.IsAssignableFrom(typeof(BaseItem)) && p[2].ParameterType == typeof(ItemUpdateType))
                    {
                        // (BaseItem item, BaseItem? parent, ItemUpdateType updateType, bool notify)
                        m.Invoke(lmObj, new object?[] { person, null, ItemUpdateType.ImageUpdate, true });
                        return;
                    }
                }
                catch
                {
                    // try next overload
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Non-fatal: failed to notify library manager after image save for {Name}", person.Name);
        }
    }
}