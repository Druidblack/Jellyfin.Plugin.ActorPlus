using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ActorPlus.Services;

public sealed class TmdbPersonClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbPersonClient> _logger;

    public TmdbPersonClient(IHttpClientFactory httpClientFactory, ILogger<TmdbPersonClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<(DateOnly? birth, DateOnly? death, string? placeOfBirth)> FetchBirthDeathAsync(int tmdbPersonId, string apiKey, CancellationToken ct)
    {
        if (tmdbPersonId <= 0 || string.IsNullOrWhiteSpace(apiKey))
        {
            return (null, null, null);
        }

        try
        {
            // TMDB v3 endpoint
            var url = $"https://api.themoviedb.org/3/person/{tmdbPersonId}?api_key={Uri.EscapeDataString(apiKey)}";

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return (null, null, null);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var birthday = doc.RootElement.TryGetProperty("birthday", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString()
                : null;
            var deathday = doc.RootElement.TryGetProperty("deathday", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            var pob = doc.RootElement.TryGetProperty("place_of_birth", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

            DateOnly? birth = TryParseDateOnly(birthday);
            DateOnly? death = TryParseDateOnly(deathday);

            return (birth, death, string.IsNullOrWhiteSpace(pob) ? null : pob.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TMDB fetch failed for person {PersonId}", tmdbPersonId);
            return (null, null, null);
        }
    }

    private static DateOnly? TryParseDateOnly(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        // TMDB returns yyyy-MM-dd
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : null;
    }
}
