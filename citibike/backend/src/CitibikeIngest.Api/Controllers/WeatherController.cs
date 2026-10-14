using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Mappers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CitibikeIngest.Api.Controllers;

[ApiController]
[Route("api/weather")]
public sealed class WeatherController(IWeatherManager manager) : ControllerBase
{
    /// <summary>
    /// Fetches the daily weather from NOAA and stores it (201 with the load). Takes seconds, so it completes within
    /// the request; 502 when NOAA fails. Requires X-Admin-Token.
    /// </summary>
    [HttpPost("runs")]
    public async Task<ActionResult<WeatherRunDto>> Load(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StartWeatherRunRequestDto? request,
        CancellationToken ct)
    {
        var run = await manager.LoadAsync(request?.From, request?.To, request?.Trigger, ct);
        return CreatedAtAction(nameof(GetRun), new { id = run.Id }, run.ToDto());
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<WeatherRunDto>>> ListRuns([FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var runs = await manager.ListRunsAsync(limit, ct);
        return Ok(runs.Select(r => r.ToDto()).ToList());
    }

    [HttpGet("runs/{id:long}")]
    public async Task<ActionResult<WeatherRunDto>> GetRun(long id, CancellationToken ct)
    {
        var run = await manager.GetRunAsync(id, ct);
        return Ok(run.ToDto());
    }

    /// <summary>Stored days in [from, to], for example ?from=2026-08-01&amp;to=2026-08-31.</summary>
    [HttpGet("days")]
    public async Task<ActionResult<WeatherDaysDto>> GetDays(
        [FromQuery, BindRequired] DateOnly from, [FromQuery, BindRequired] DateOnly to, CancellationToken ct)
    {
        var days = await manager.GetDaysAsync(from, to, ct);
        return Ok(days.ToDto());
    }
}
