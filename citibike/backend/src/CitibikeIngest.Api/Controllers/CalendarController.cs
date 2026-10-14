using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Mappers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CitibikeIngest.Api.Controllers;

[ApiController]
[Route("api/calendar")]
public sealed class CalendarController(ICalendarManager manager) : ControllerBase
{
    /// <summary>
    /// Exports the holidays from the pinned `holidays` package and replaces the years of the load (201 with the
    /// load). Takes about a second, so it completes within the request; 502 when the export fails. Requires X-Admin-Token.
    /// </summary>
    [HttpPost("runs")]
    public async Task<ActionResult<CalendarRunDto>> Load(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StartCalendarRunRequestDto? request,
        CancellationToken ct)
    {
        var run = await manager.LoadAsync(request?.FromYear, request?.ToYear, request?.Trigger, ct);
        return CreatedAtAction(nameof(GetRun), new { id = run.Id }, run.ToDto());
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<CalendarRunDto>>> ListRuns([FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var runs = await manager.ListRunsAsync(limit, ct);
        return Ok(runs.Select(r => r.ToDto()).ToList());
    }

    [HttpGet("runs/{id:long}")]
    public async Task<ActionResult<CalendarRunDto>> GetRun(long id, CancellationToken ct)
    {
        var run = await manager.GetRunAsync(id, ct);
        return Ok(run.ToDto());
    }

    /// <summary>Stored holidays in [from, to], for example ?from=2022-12-01&amp;to=2022-12-31.</summary>
    [HttpGet("holidays")]
    public async Task<ActionResult<HolidaysDto>> GetHolidays(
        [FromQuery, BindRequired] DateOnly from, [FromQuery, BindRequired] DateOnly to, CancellationToken ct)
    {
        var holidays = await manager.GetHolidaysAsync(from, to, ct);
        return Ok(holidays.ToDto());
    }
}
