using System.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.Services;

public sealed class BirthDateCacheStore
{
    private readonly ILogger<BirthDateCacheStore> _logger;
    private readonly string _cacheFilePath;
    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();
    private int _loaded;

    private readonly object _saveTimerLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Timer? _saveTimer;
    private int _savePending;

    public BirthDateCacheStore(ILogger<BirthDateCacheStore> logger)
    {
        _logger = logger;
        var plugin = Plugin.Instance;
        _cacheFilePath = plugin != null
            ? Path.Combine(plugin.DataFolderPath, "birthdates_cache.json")
            : Path.Combine(AppContext.BaseDirectory, "birthdates_cache.json");
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1)
        {
            return;
        }

        try
        {
            _logger.LogDebug("ActorPlus cache file: {Path}", _cacheFilePath);
            if (!File.Exists(_cacheFilePath))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(_cacheFilePath, ct).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<Dictionary<Guid, CacheEntry>>(json, JsonOptions());
            if (data is null)
            {
                return;
            }

            foreach (var kv in data)
            {
                _cache[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cache file {Path}", _cacheFilePath);
        }
    }

    public bool TryGet(Guid personId, out CacheEntry entry)
    {
        return _cache.TryGetValue(personId, out entry!);
    }

    public void Set(Guid personId, CacheEntry entry)
    {
        _cache[personId] = entry;
        QueueSave();
    }

    private void QueueSave()
    {
        // Debounce disk writes: many Set() calls can happen back-to-back (e.g., batch overlay requests).
        Interlocked.Exchange(ref _savePending, 1);
        lock (_saveTimerLock)
        {
            if (_saveTimer == null)
            {
                _saveTimer = new Timer(_ => _ = SavePendingAsync(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            }
            else
            {
                _saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task SavePendingAsync()
    {
        if (Interlocked.Exchange(ref _savePending, 0) == 0)
        {
            return;
        }

        await _saveGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await SaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BirthDateCacheStore] Failed to save cache.");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("ActorPlus cache file: {Path}", _cacheFilePath);
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                Directory.CreateDirectory(plugin.DataFolderPath);
            }

            var json = JsonSerializer.Serialize(_cache, JsonOptions());
            await File.WriteAllTextAsync(_cacheFilePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cache file {Path}", _cacheFilePath);
        }
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
        };
    }

    public sealed record CacheEntry
    {
        public string? BirthDate { get; init; } // yyyy-MM-dd
        public string? DeathDate { get; init; } // yyyy-MM-dd
        public string? BirthPlace { get; init; }
        public string? BirthCountryIso2 { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
        public string Source { get; init; } = "unknown";
    }
}
