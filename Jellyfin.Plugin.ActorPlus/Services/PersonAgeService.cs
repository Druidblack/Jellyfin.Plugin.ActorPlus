using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.Services;

public sealed class PersonAgeService
{
    private readonly ILibraryManager _libraryManager;
    private readonly BirthDateCacheStore _cache;
    private readonly TmdbPersonClient _tmdb;
    private readonly CountryCodeMapper _countryCodeMapper;
    private readonly ILogger<PersonAgeService> _logger;

    public PersonAgeService(
        ILibraryManager libraryManager,
        BirthDateCacheStore cache,
        TmdbPersonClient tmdb,
        CountryCodeMapper countryCodeMapper,
        ILogger<PersonAgeService> logger)
    {
        _libraryManager = libraryManager;
        _cache = cache;
        _tmdb = tmdb;
        _countryCodeMapper = countryCodeMapper;
        _logger = logger;
    }

    public async Task<AgeInfo?> GetAgeAsync(Guid personId, CancellationToken ct)
    {
        await _cache.EnsureLoadedAsync(ct).ConfigureAwait(false);

        var nowDate = DateOnly.FromDateTime(DateTime.Now);

        // 1) cache
        if (_cache.TryGet(personId, out var cached))
        {
            var cfg = Plugin.Instance?.Configuration;
            var expired = false;

            if (cfg != null)
            {
                var ttl = TimeSpan.FromDays(Math.Clamp(cfg.CacheTtlDays, 1, 3650));
                expired = cached.Source == "tmdb" && (DateTimeOffset.UtcNow - cached.UpdatedUtc) > ttl;
            }

            if (!expired)
            {
                // Cache enrichment: older cache entries may not have birthplace / ISO2 yet.
                // If the flag feature is enabled and the cache lacks country data, try to fill it from Jellyfin metadata
                // (or TMDB fallback if configured). This mirrors the "open actor page" behavior without forcing users to do it.
                if (cfg?.ShowBirthCountryFlag == true &&
                    (string.IsNullOrWhiteSpace(cached.BirthPlace) || string.IsNullOrWhiteSpace(cached.BirthCountryIso2)))
                {
                    try
                    {
                        string? birthPlace = cached.BirthPlace;
                        string? iso2 = cached.BirthCountryIso2;

                        var personItem = _libraryManager.GetItemById(personId);
                        if (personItem is Person person2)
                        {
                            if (string.IsNullOrWhiteSpace(birthPlace))
                            {
                                birthPlace = TryGetPersonBirthPlace(person2);
                            }

                            if (string.IsNullOrWhiteSpace(iso2) && !string.IsNullOrWhiteSpace(birthPlace))
                            {
                                iso2 = _countryCodeMapper.BirthPlaceToIso2(birthPlace);
                            }

                            // Optional: TMDB fallback to get place_of_birth if Jellyfin doesn't have it
                            if (string.IsNullOrWhiteSpace(birthPlace) && cfg.UseTmdbFallback && !string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
                            {
                                if (person2.ProviderIds != null &&
                                    person2.ProviderIds.TryGetValue("Tmdb", out var tmdbIdRaw) &&
                                    int.TryParse(tmdbIdRaw, out var tmdbId))
                                {
                                    var (_, _, p) = await _tmdb.FetchBirthDeathAsync(tmdbId, cfg.TmdbApiKey, ct).ConfigureAwait(false);
                                    if (!string.IsNullOrWhiteSpace(p))
                                    {
                                        birthPlace = p;
                                        if (string.IsNullOrWhiteSpace(iso2))
                                        {
                                            iso2 = _countryCodeMapper.BirthPlaceToIso2(p);
                                        }
                                    }
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(birthPlace) || !string.IsNullOrWhiteSpace(iso2))
                        {
                            var updated = cached with
                            {
                                BirthPlace = string.IsNullOrWhiteSpace(cached.BirthPlace) ? birthPlace : cached.BirthPlace,
                                BirthCountryIso2 = string.IsNullOrWhiteSpace(cached.BirthCountryIso2) ? iso2 : cached.BirthCountryIso2,
                                // keep UpdatedUtc and Source as-is to avoid changing TTL behavior
                            };
                            _cache.Set(personId, updated);
                            cached = updated;
                        }
                    }
                    catch
                    {
                        // ignore enrichment errors
                    }
                }

                return BuildAgeInfo(personId, cached, nowDate, cacheHit: true);
            }

            // expired; continue to refresh (TMDB-only TTL)
        }

        // 2) Jellyfin person metadata
        var item = _libraryManager.GetItemById(personId);
        if (item is not Person person)
        {
            return null;
        }

        var birthFromJellyfin = TryGetPersonBirthDate(person);
        var deathFromJellyfin = TryGetPersonDeathDate(person);
        var birthPlaceFromJellyfin = TryGetPersonBirthPlace(person);
        var birthIso2FromJellyfin = _countryCodeMapper.BirthPlaceToIso2(birthPlaceFromJellyfin);

        if (birthFromJellyfin != null || deathFromJellyfin != null || !string.IsNullOrWhiteSpace(birthPlaceFromJellyfin))
        {
            var entry = new BirthDateCacheStore.CacheEntry
            {
                BirthDate = birthFromJellyfin?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DeathDate = deathFromJellyfin?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                BirthPlace = birthPlaceFromJellyfin,
                BirthCountryIso2 = birthIso2FromJellyfin,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Source = "jellyfin",
            };
            _cache.Set(personId, entry);
            return BuildAgeInfo(personId, entry, nowDate, cacheHit: false);
        }

        // 3) TMDB fallback
        var cfg2 = Plugin.Instance?.Configuration;
        if (cfg2?.UseTmdbFallback == true && !string.IsNullOrWhiteSpace(cfg2.TmdbApiKey))
        {
            if (person.ProviderIds != null && person.ProviderIds.TryGetValue("Tmdb", out var tmdbIdRaw) && int.TryParse(tmdbIdRaw, out var tmdbId))
            {
                var (b, d, p) = await _tmdb.FetchBirthDeathAsync(tmdbId, cfg2.TmdbApiKey, ct).ConfigureAwait(false);

                var iso2 = _countryCodeMapper.BirthPlaceToIso2(p);

                if (b != null || d != null || !string.IsNullOrWhiteSpace(p))
                {
                    var entry = new BirthDateCacheStore.CacheEntry
                    {
                        BirthDate = b?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        DeathDate = d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        BirthPlace = p,
                        BirthCountryIso2 = iso2,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        Source = "tmdb",
                    };
                    _cache.Set(personId, entry);
                    return BuildAgeInfo(personId, entry, nowDate, cacheHit: false);
                }
            }
        }

        return null;
    }

    public async Task<Dictionary<Guid, AgeInfo>> GetAgesAsync(IEnumerable<Guid> personIds, CancellationToken ct)
    {
        var dict = new Dictionary<Guid, AgeInfo>();
        foreach (var id in personIds)
        {
            if (id == Guid.Empty)
            {
                continue;
            }

            try
            {
                var info = await GetAgeAsync(id, ct).ConfigureAwait(false);
                if (info != null)
                {
                    dict[id] = info;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get age for person {PersonId}", id);
            }
        }

        return dict;
    }

    private AgeInfo BuildAgeInfo(Guid personId, BirthDateCacheStore.CacheEntry entry, DateOnly now, bool cacheHit)
    {
        var birth = TryParse(entry.BirthDate);
        var death = TryParse(entry.DeathDate);

        // Backward-compat: older cache versions may not have ISO2 computed yet.
        var iso2 = entry.BirthCountryIso2;
        if (string.IsNullOrWhiteSpace(iso2) && !string.IsNullOrWhiteSpace(entry.BirthPlace))
        {
            iso2 = _countryCodeMapper.BirthPlaceToIso2(entry.BirthPlace);
        }

        int? age = null;
        bool deceased = false;

        if (birth != null)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg?.ShowAgeAtDeath == true && death != null)
            {
                deceased = true;
                age = ComputeAge(birth.Value, death.Value);
            }
            else
            {
                if (death != null)
                {
                    deceased = true;
                }
                age = ComputeAge(birth.Value, now);
            }
        }

        return new AgeInfo
        {
            PersonId = personId,
            BirthDate = birth,
            DeathDate = death,
            AgeYears = age,
            IsDeceased = deceased,
            BirthPlace = entry.BirthPlace,
            BirthCountryIso2 = iso2,
            Source = entry.Source,
            CacheHit = cacheHit,
        };
    }

    private static DateOnly? TryGetPersonBirthDate(Person person)
    {
        // Primary: Jellyfin commonly stores Person "Born" in BaseItem.PremiereDate.
        var dt = person.PremiereDate;
        if (dt != null)
        {
            return DateOnly.FromDateTime(dt.Value.Date);
        }

        // Fallbacks: some builds/patches may expose alternative properties on Person.
        // We use reflection to avoid hard dependency on a particular server build.
        // Supported shapes:
        // - DateTime / DateTimeOffset
        // - string in ISO formats (yyyy-MM-dd / yyyy-MM-ddTHH:mm:ss...)
        if (TryGetDateOnlyViaReflection(person, new[] { "BirthDate", "DateOfBirth", "Birthday" }, out var d))
        {
            return d;
        }

        // Last-resort: if only the year is known, some metadata may store it in ProductionYear.
        // Treat it as Jan 1 of that year (still better than nothing).
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

        // Fallbacks: alternative fields (some builds/plugins expose these).
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

            object? value;
            try
            {
                value = prop.GetValue(instance);
            }
            catch
            {
                continue;
            }

            var d = TryConvertToDateOnly(value);
            if (d != null)
            {
                date = d;
                return true;
            }
        }

        return false;
    }

    private static DateOnly? TryConvertToDateOnly(object? value)
    {
        if (value == null)
        {
            return null;
        }

        switch (value)
        {
            case DateOnly d:
                return d;
            case DateTime dt:
                return DateOnly.FromDateTime(dt.Date);
            case DateTimeOffset dto:
                return DateOnly.FromDateTime(dto.DateTime.Date);
            case string s:
                {
                    s = s.Trim();
                    if (s.Length >= 10)
                    {
                        var first10 = s.Substring(0, 10);
                        if (DateOnly.TryParseExact(first10, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
                        {
                            return d2;
                        }
                    }

                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt2))
                    {
                        return DateOnly.FromDateTime(dt2.Date);
                    }

                    return null;
                }
            default:
                return null;
        }
    }

    private static DateOnly? TryParse(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return null;
        }

        return DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static int ComputeAge(DateOnly birth, DateOnly asOf)
    {
        var years = asOf.Year - birth.Year;
        if (asOf < birth.AddYears(years))
        {
            years--;
        }

        return years;
    }

    public sealed class AgeInfo
    {
        public Guid PersonId { get; init; }
        public DateOnly? BirthDate { get; init; }
        public DateOnly? DeathDate { get; init; }
        public int? AgeYears { get; init; }
        public bool IsDeceased { get; init; }
        public string? BirthPlace { get; init; }
        public string? BirthCountryIso2 { get; init; }
        public string Source { get; init; } = "unknown";
        public bool CacheHit { get; init; }

        public string? AgeText => AgeYears?.ToString(CultureInfo.InvariantCulture);
    }
}
