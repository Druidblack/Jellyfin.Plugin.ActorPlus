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
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.ScheduledTasks;

/// <summary>
/// Rebuilds ActorPlus' persistent person-data cache from the metadata currently stored in Jellyfin.
/// This task does not invoke remote metadata providers and does not download images.
/// </summary>
public sealed class RebuildActorCacheTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly BirthDateCacheStore _cacheStore;
    private readonly CountryCodeMapper _countryCodeMapper;
    private readonly ILogger<RebuildActorCacheTask> _logger;

    public RebuildActorCacheTask(
        ILibraryManager libraryManager,
        BirthDateCacheStore cacheStore,
        CountryCodeMapper countryCodeMapper,
        ILogger<RebuildActorCacheTask> logger)
    {
        _libraryManager = libraryManager;
        _cacheStore = cacheStore;
        _countryCodeMapper = countryCodeMapper;
        _logger = logger;
    }

    public string Name => "ActorPlus: Rebuild actor data cache";

    public string Key => "ActorPlus_RebuildActorDataCache";

    public string Description => "Rebuilds ActorPlus cached birth dates, death dates and birth places from the current Jellyfin actor metadata.";

    public string Category => "ActorPlus";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Keep this opt-in. It can be scheduled from Jellyfin's Scheduled Tasks UI if desired.
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

        var rebuilt = new Dictionary<Guid, BirthDateCacheStore.CacheEntry>(people.Count);
        var withData = 0;
        var withoutData = 0;

        _logger.LogInformation("[ActorPlus] Rebuilding actor data cache from Jellyfin metadata. People found: {Count}", people.Count);

        for (var i = 0; i < people.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var person = people[i];
            var entry = CreateEntry(person);
            if (entry is null)
            {
                // Intentionally do not carry an old entry forward. If a birth date/place was
                // removed in Jellyfin, the old ActorPlus value must disappear as well.
                withoutData++;
            }
            else
            {
                rebuilt[person.Id] = entry;
                withData++;
            }

            progress.Report((i + 1) * 95.0 / Math.Max(people.Count, 1));
        }

        // One atomic-style replacement instead of thousands of individual Set()/disk writes.
        // Entries for deleted actors and metadata that was cleared in Jellyfin disappear here.
        await _cacheStore.ReplaceAllAsync(rebuilt, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
        _logger.LogInformation(
            "[ActorPlus] Actor data cache rebuild complete. Cached: {Cached}; without local birth/death/place metadata: {WithoutData}; revision: {Revision}",
            withData,
            withoutData,
            _cacheStore.Revision);
    }

    private BirthDateCacheStore.CacheEntry? CreateEntry(Person person)
    {
        var birth = TryGetPersonBirthDate(person);
        var death = TryGetPersonDeathDate(person);
        var birthPlace = TryGetPersonBirthPlace(person);

        if (birth == null && death == null && string.IsNullOrWhiteSpace(birthPlace))
        {
            return null;
        }

        return new BirthDateCacheStore.CacheEntry
        {
            BirthDate = birth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DeathDate = death?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            BirthPlace = birthPlace,
            BirthCountryIso2 = _countryCodeMapper.BirthPlaceToIso2(birthPlace),
            UpdatedUtc = DateTimeOffset.UtcNow,
            Source = "jellyfin",
        };
    }

    private static DateOnly? TryGetPersonBirthDate(Person person)
    {
        var dt = person.PremiereDate;
        if (dt != null)
        {
            return DateOnly.FromDateTime(dt.Value.Date);
        }

        if (TryGetDateOnlyViaReflection(person, new[] { "BirthDate", "DateOfBirth", "Birthday" }, out var d))
        {
            return d;
        }

        if (person.ProductionYear.HasValue && person.ProductionYear.Value is >= 1850 and <= 2500)
        {
            try
            {
                return new DateOnly(person.ProductionYear.Value, 1, 1);
            }
            catch
            {
                // Ignore invalid year values.
            }
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

        return TryGetDateOnlyViaReflection(person, new[] { "DeathDate", "DateOfDeath", "Deathday", "Died" }, out var d)
            ? d
            : null;
    }

    private static string? TryGetPersonBirthPlace(Person person)
    {
        try
        {
            var locations = person.ProductionLocations;
            if (locations is { Length: > 0 })
            {
                var joined = string.Join(", ", locations);
                if (!string.IsNullOrWhiteSpace(joined))
                {
                    return joined.Trim();
                }
            }
        }
        catch
        {
            // Fall through to compatibility properties.
        }

        return TryGetStringViaReflection(person, new[] { "BirthPlace", "Birthplace", "PlaceOfBirth", "BirthLocation" }, out var value)
            ? value
            : null;
    }

    private static bool TryGetStringViaReflection(object instance, string[] propertyNames, out string? value)
    {
        value = null;
        var type = instance.GetType();
        foreach (var name in propertyNames)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null)
            {
                continue;
            }

            object? raw;
            try
            {
                raw = property.GetValue(instance);
            }
            catch
            {
                continue;
            }

            if (raw is string text && !string.IsNullOrWhiteSpace(text))
            {
                value = text.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryGetDateOnlyViaReflection(object instance, string[] propertyNames, out DateOnly? date)
    {
        date = null;
        var type = instance.GetType();
        foreach (var name in propertyNames)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null)
            {
                continue;
            }

            object? raw;
            try
            {
                raw = property.GetValue(instance);
            }
            catch
            {
                continue;
            }

            try
            {
                if (raw is DateTime dt)
                {
                    date = DateOnly.FromDateTime(dt.Date);
                    return true;
                }

                if (raw is DateTimeOffset dto)
                {
                    date = DateOnly.FromDateTime(dto.Date);
                    return true;
                }

                if (raw is string text && !string.IsNullOrWhiteSpace(text) &&
                    DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                {
                    date = DateOnly.FromDateTime(parsed.Date);
                    return true;
                }
            }
            catch
            {
                // Ignore incompatible compatibility property.
            }
        }

        return false;
    }
}
