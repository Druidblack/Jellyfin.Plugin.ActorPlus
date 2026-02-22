using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ActorPlus.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.ScheduledTasks;

/// <summary>
/// Scheduled task that forces metadata refresh for all Person items.
/// This can be used to pull missing/updated birth dates, death dates, birth places, etc.
/// </summary>
public sealed class RefreshPersonMetadataTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly BirthDateCacheStore _cacheStore;
    private readonly CountryCodeMapper _countryCodeMapper;
    private readonly ILogger<RefreshPersonMetadataTask> _logger;

    public RefreshPersonMetadataTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        BirthDateCacheStore cacheStore,
        CountryCodeMapper countryCodeMapper,
        ILogger<RefreshPersonMetadataTask> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _cacheStore = cacheStore;
        _countryCodeMapper = countryCodeMapper;
        _logger = logger;
    }

    public string Name => "ActorPlus: Update the metadata of the actors";

    public string Key => "ActorPlus_RefreshPersonMetadata";

    public string Description => "Forcibly updates metadata for all actors and synchronizes the ActorPlus cache.";

    public string Category => "ActorPlus";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No automatic schedule. User runs it manually when needed.
        return Array.Empty<TaskTriggerInfo>();
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _cacheStore.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var people = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Person },
            Recursive = true,
        }).OfType<Person>().ToList();

        if (people.Count == 0)
        {
            progress.Report(100);
            return;
        }

        _logger.LogInformation("[ActorPlus] RefreshPersonMetadataTask: найдено актёров: {Count}", people.Count);

        // This is how Jellyfin internally builds refresh options for API refresh calls.
        // We keep it resilient across server versions by setting additional fields via reflection.
        var directoryService = new DirectoryService(_fileSystem);
        var refreshOptions = new MetadataRefreshOptions(directoryService);
        ConfigureRefreshOptions(refreshOptions);

        for (var i = 0; i < people.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var person = people[i];
            try
            {
                // Forces metadata refresh. This is the same method used by Jellyfin for other refresh workflows.
                await person.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(false);

                // After refresh, update plugin cache so overlays pick up new data immediately.
                UpdateCacheEntryFromPerson(person);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ActorPlus] RefreshPersonMetadataTask: ошибка обновления метаданных для {Name} ({Id})", person.Name, person.Id);
            }

            progress.Report((i + 1) * 100.0 / people.Count);
        }

        _logger.LogInformation("[ActorPlus] RefreshPersonMetadataTask: завершено");
    }

    private void ConfigureRefreshOptions(object refreshOptions)
    {
        // We try to mirror "Replace all metadata" behavior without hard-binding to exact server API.
        // If a property does not exist on this server build, we silently ignore it.

        TrySetProperty(refreshOptions, "ReplaceAllMetadata", true);
        TrySetProperty(refreshOptions, "ReplaceAllImages", false);
        TrySetProperty(refreshOptions, "IsAutomated", true);

        // Prefer a full refresh when available.
        // Typical enum values: Default, FullRefresh.
        TrySetEnumProperty(refreshOptions, "MetadataRefreshMode", new[] { "FullRefresh", "Default" });

        // Avoid changing images (metadata only).
        // Typical enum values: None, Default.
        TrySetEnumProperty(refreshOptions, "ImageRefreshMode", new[] { "None", "Default" });
    }

    private static void TrySetProperty(object instance, string propertyName, object value)
    {
        try
        {
            var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null || !prop.CanWrite)
            {
                return;
            }

            var targetType = prop.PropertyType;
            if (value != null && !targetType.IsAssignableFrom(value.GetType()))
            {
                // Best-effort conversion for simple types.
                if (targetType == typeof(bool) && value is bool b)
                {
                    prop.SetValue(instance, b);
                    return;
                }

                return;
            }

            prop.SetValue(instance, value);
        }
        catch
        {
            // ignore
        }
    }

    private static void TrySetEnumProperty(object instance, string propertyName, IReadOnlyList<string> preferredNames)
    {
        try
        {
            var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null || !prop.CanWrite)
            {
                return;
            }

            var enumType = prop.PropertyType;
            if (!enumType.IsEnum)
            {
                return;
            }

            foreach (var name in preferredNames)
            {
                try
                {
                    var parsed = Enum.Parse(enumType, name, ignoreCase: true);
                    prop.SetValue(instance, parsed);
                    return;
                }
                catch
                {
                    // try next
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private void UpdateCacheEntryFromPerson(Person person)
    {
        try
        {
            var birth = TryGetPersonBirthDate(person);
            var death = TryGetPersonDeathDate(person);
            var birthPlace = TryGetPersonBirthPlace(person);
            var iso2 = _countryCodeMapper.BirthPlaceToIso2(birthPlace);

            if (birth == null && death == null && string.IsNullOrWhiteSpace(birthPlace))
            {
                return;
            }

            var entry = new BirthDateCacheStore.CacheEntry
            {
                BirthDate = birth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DeathDate = death?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                BirthPlace = birthPlace,
                BirthCountryIso2 = iso2,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Source = "jellyfin",
            };

            _cacheStore.Set(person.Id, entry);
        }
        catch
        {
            // ignore
        }
    }

    private static DateOnly? TryGetPersonBirthDate(Person person)
    {
        // Primary: Jellyfin commonly stores Person "Born" in BaseItem.PremiereDate.
        var dt = person.PremiereDate;
        if (dt != null)
        {
            return DateOnly.FromDateTime(dt.Value.Date);
        }

        if (TryGetDateOnlyViaReflection(person, new[] { "BirthDate", "DateOfBirth", "Birthday" }, out var d))
        {
            return d;
        }

        // Last-resort: year only.
        if (person.ProductionYear.HasValue && person.ProductionYear.Value is >= 1850 and <= 2500)
        {
            try { return new DateOnly(person.ProductionYear.Value, 1, 1); } catch { /* ignore */ }
        }

        return null;
    }

    private static DateOnly? TryGetPersonDeathDate(Person person)
    {
        var dt = person.EndDate;
        if (dt != null)
        {
            return DateOnly.FromDateTime(dt.Value.Date);
        }

        if (TryGetDateOnlyViaReflection(person, new[] { "DeathDate", "DateOfDeath", "Deathday", "Died" }, out var d))
        {
            return d;
        }

        return null;
    }

    private static string? TryGetPersonBirthPlace(Person person)
    {
        try
        {
            // Primary: BaseItem.ProductionLocations is commonly used in Jellyfin for Person "Birthplace".
            var locs = person.ProductionLocations;
            if (locs is { Length: > 0 })
            {
                var joined = string.Join(", ", locs);
                return string.IsNullOrWhiteSpace(joined) ? null : joined.Trim();
            }
        }
        catch
        {
            // ignore
        }

        if (TryGetStringViaReflection(person, new[] { "BirthPlace", "Birthplace", "PlaceOfBirth", "BirthLocation" }, out var s))
        {
            return s;
        }

        return null;
    }

    private static bool TryGetStringViaReflection(object instance, string[] propertyNames, out string? value)
    {
        value = null;
        var t = instance.GetType();
        foreach (var name in propertyNames)
        {
            var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null)
            {
                continue;
            }

            object? v;
            try { v = prop.GetValue(instance); } catch { continue; }

            if (v is string s && !string.IsNullOrWhiteSpace(s))
            {
                value = s.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryGetDateOnlyViaReflection(object instance, string[] propertyNames, out DateOnly? date)
    {
        date = null;
        var t = instance.GetType();
        foreach (var name in propertyNames)
        {
            var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null)
            {
                continue;
            }

            object? v;
            try { v = prop.GetValue(instance); } catch { continue; }

            try
            {
                if (v is DateTime dt)
                {
                    date = DateOnly.FromDateTime(dt.Date);
                    return true;
                }

                if (v is DateTimeOffset dto)
                {
                    date = DateOnly.FromDateTime(dto.Date);
                    return true;
                }

                if (v is string s && !string.IsNullOrWhiteSpace(s))
                {
                    // Accept common formats: yyyy-MM-dd or full ISO.
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                    {
                        date = DateOnly.FromDateTime(parsed.Date);
                        return true;
                    }
                }
            }
            catch
            {
                // ignore this property
            }
        }

        return false;
    }
}
