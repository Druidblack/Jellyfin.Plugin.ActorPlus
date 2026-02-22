using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ActorPlus.Services;

/// <summary>
/// Maps country names (in many languages) to ISO 3166-1 alpha-2 codes.
/// The underlying mapping data comes from the user-provided list.
/// Keys in the mapping are stored in normalized form.
/// </summary>
public sealed class CountryCodeMapper
{
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    // Regions/continents that may appear at the end of a birthplace string; we should ignore them.
    private static readonly HashSet<string> Ignore = new(StringComparer.Ordinal)
    {
        "africa", "asia", "europe", "oceania", "antarctica",
        "north america", "south america", "central america", "latin america", "america",
        "caribbean", "middle east", "eurasia",
        "eu", "e u", "european union",
        "cis", "commonwealth of independent states",
        "европа", "евросоюз", "ес", "африка", "азия", "океания", "антарктида",
        "северная америка", "южная америка", "центральная америка", "латинская америка", "америка", "карибы",
    };

    private readonly Lazy<Dictionary<string, string>> _map;

    public CountryCodeMapper()
    {
        _map = new Lazy<Dictionary<string, string>>(LoadMap, isThreadSafe: true);
    }

    /// <summary>
    /// Attempts to extract a country ISO2 code from a free-form birthplace string.
    /// </summary>
    public string? BirthPlaceToIso2(string? birthPlace)
    {
        var raw = (birthPlace ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var map = _map.Value;

        // Common format: City, Region, Country
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var np = Normalize(parts[i]);
            if (np.Length == 0)
            {
                continue;
            }

            if (Ignore.Contains(np))
            {
                continue;
            }

            if (map.TryGetValue(np, out var iso2))
            {
                return iso2;
            }
        }

        // Fallback: treat the whole string as a country name.
        var full = Normalize(raw);
        if (full.Length > 0 && map.TryGetValue(full, out var iso2Full))
        {
            return iso2Full;
        }

        return null;
    }

    /// <summary>
    /// Normalizes a country name similarly to the multi_tag.js logic:
    /// - remove diacritics
    /// - lowercase
    /// - replace ё -> е
    /// - replace punctuation/brackets with spaces
    /// - collapse whitespace
    /// </summary>
    public static string Normalize(string? s)
    {
        var t = (s ?? string.Empty);
        if (t.Length == 0)
        {
            return string.Empty;
        }

        // Remove diacritics
        t = t.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        t = sb.ToString().Normalize(NormalizationForm.FormC);

        t = t.ToLowerInvariant();
        t = t.Replace('ё', 'е');
        t = t.Replace('’', '\'');
        t = t.Replace('`', '\'');

        // Replace brackets/quotes with spaces
        t = Regex.Replace(t, "[()\\[\\]{}\"'`]", " ");
        t = t.Replace('.', ' ');
        t = t.Replace('-', ' ').Replace('–', ' ').Replace('—', ' ');
        t = t.Replace("&", " and ");
        t = MultiSpace.Replace(t, " ").Trim();

        return t;
    }

    private static Dictionary<string, string> LoadMap()
    {
        // Embedded resource: Resources/country_iso2_map.json
        var asm = Assembly.GetExecutingAssembly();
        var name = FindResourceName(asm, "country_iso2_map.json");
        if (name == null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return dict ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static string? FindResourceName(Assembly asm, string suffix)
    {
        foreach (var n in asm.GetManifestResourceNames())
        {
            if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return n;
            }
        }
        return null;
    }
}
