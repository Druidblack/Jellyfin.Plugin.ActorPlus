using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ActorPlus.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ActorPlus.Controllers;

[ApiController]
[Route("ActorPlus")]
public class BirthAgeController : ControllerBase
{
    private readonly PersonAgeService _ageService;

    public BirthAgeController(PersonAgeService ageService)
    {
        _ageService = ageService;
    }

    [HttpGet("status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var cfg = Plugin.Instance?.Configuration;
        return new StatusResponse
        {
            Enabled = cfg?.EnableOverlay ?? false,
            UseTmdbFallback = cfg?.UseTmdbFallback ?? false,
            ShowAgeAtRelease = cfg?.ShowAgeAtRelease ?? true,
            ShowAgeIcons = cfg?.ShowAgeIcons ?? false,
            ShowBirthCountryFlag = cfg?.ShowBirthCountryFlag ?? true,
            ShowBirthPlaceText = cfg?.ShowBirthPlaceText ?? false,
            ShowDeceasedOverlay = cfg?.ShowDeceasedOverlay ?? false,
            EnableHoverFilmography = cfg?.EnableHoverFilmography ?? false,
            RandomizeHoverFilmography = cfg?.RandomizeHoverFilmography ?? false,
            HoverFilmographyLimit = cfg?.HoverFilmographyLimit ?? 12,
            EnableHoverCastMenu = cfg?.EnableHoverCastMenu ?? false,
            HoverCastLimit = cfg?.HoverCastLimit ?? 12,
        };
    }

    [Authorize]
    [HttpGet("age")]
    public async Task<ActionResult<PersonAgeDto>> GetAge([FromQuery] Guid personId, CancellationToken ct)
    {
        if (personId == Guid.Empty)
        {
            return BadRequest();
        }

        var info = await _ageService.GetAgeAsync(personId, ct).ConfigureAwait(false);
        if (info == null)
        {
            return NotFound();
        }

        return new PersonAgeDto(info);
    }


    [Authorize]
    [HttpGet("debug")]
    public async Task<ActionResult<DebugResponse>> Debug([FromQuery] Guid personId, CancellationToken ct)
    {
        if (personId == Guid.Empty)
        {
            return BadRequest();
        }

        // We call GetAgeAsync to ensure cache load and consistent behavior, but we also return what we can read from Jellyfin directly.
        var info = await _ageService.GetAgeAsync(personId, ct).ConfigureAwait(false);

        return new DebugResponse
        {
            PersonId = personId,
            Found = info != null,
            BirthDate = info?.BirthDate?.ToString("yyyy-MM-dd"),
            DeathDate = info?.DeathDate?.ToString("yyyy-MM-dd"),
            AgeYears = info?.AgeYears,
            IsDeceased = info?.IsDeceased ?? false,
            Source = info?.Source ?? "none",
            CacheHit = info?.CacheHit ?? false,
            BirthPlace = info?.BirthPlace,
            BirthCountryIso2 = info?.BirthCountryIso2,
        };
    }

    [Authorize]
    [HttpPost("ages")]
    public async Task<ActionResult<Dictionary<Guid, PersonAgeDto>>> GetAges([FromBody] BatchRequest request, CancellationToken ct)
    {
        var ids = request?.PersonIds?.Where(x => x != Guid.Empty).Distinct().ToArray() ?? Array.Empty<Guid>();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, PersonAgeDto>();
        }

        var dict = await _ageService.GetAgesAsync(ids, ct).ConfigureAwait(false);
        return dict.ToDictionary(kv => kv.Key, kv => new PersonAgeDto(kv.Value));
    }

    public sealed class DebugResponse
    {
        public Guid PersonId { get; set; }
        public bool Found { get; set; }
        public string? BirthDate { get; set; }
        public string? DeathDate { get; set; }
        public int? AgeYears { get; set; }
        public bool IsDeceased { get; set; }
        public string Source { get; set; } = "unknown";
        public bool CacheHit { get; set; }
        public string? BirthPlace { get; set; }
        public string? BirthCountryIso2 { get; set; }
    }

    public sealed class BatchRequest
    {
        public Guid[] PersonIds { get; set; } = Array.Empty<Guid>();
    }

    public sealed class StatusResponse
    {
        public bool Enabled { get; set; }
        public bool UseTmdbFallback { get; set; }
        public bool ShowAgeAtRelease { get; set; }
        public bool ShowAgeIcons { get; set; }
        public bool ShowBirthCountryFlag { get; set; }
        public bool ShowBirthPlaceText { get; set; }
        public bool ShowDeceasedOverlay { get; set; }

        public bool EnableHoverFilmography { get; set; }
        public bool RandomizeHoverFilmography { get; set; }

        public int HoverFilmographyLimit { get; set; }

        public bool EnableHoverCastMenu { get; set; }
        public int HoverCastLimit { get; set; }
    }

    public sealed class PersonAgeDto
    {
        public Guid PersonId { get; set; }
        public string? BirthDate { get; set; }
        public string? DeathDate { get; set; }
        public int? AgeYears { get; set; }
        public bool IsDeceased { get; set; }
        public string Source { get; set; } = "unknown";
        public bool CacheHit { get; set; }
        public string? AgeText { get; set; }
        public string? BirthPlace { get; set; }
        public string? BirthCountryIso2 { get; set; }

        public PersonAgeDto() { }

        public PersonAgeDto(PersonAgeService.AgeInfo info)
        {
            PersonId = info.PersonId;
            BirthDate = info.BirthDate?.ToString("yyyy-MM-dd");
            DeathDate = info.DeathDate?.ToString("yyyy-MM-dd");
            AgeYears = info.AgeYears;
            IsDeceased = info.IsDeceased;
            Source = info.Source;
            CacheHit = info.CacheHit;
            AgeText = info.AgeText;
            BirthPlace = info.BirthPlace;
            BirthCountryIso2 = info.BirthCountryIso2;
        }
    }
}
