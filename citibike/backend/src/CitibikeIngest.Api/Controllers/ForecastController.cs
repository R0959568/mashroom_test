using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Mappers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CitibikeIngest.Api.Controllers;

[ApiController]
[Route("api/forecast")]
public sealed class ForecastController(IForecastManager manager) : ControllerBase
{
    /// <summary>Stations to pick from: ?q=central park (name, case-insensitive) or an exact key; busiest first.</summary>
    [HttpGet("stations")]
    public async Task<ActionResult<IReadOnlyList<StationDto>>> ListStations([FromQuery] string? q, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var stations = await manager.ListStationsAsync(q, limit, ct);
        return Ok(stations.Select(s => s.ToDto()).ToList());
    }

    /// <summary>The days a forecast can be asked for: the data's first and last day, and the day after it ("tomorrow").</summary>
    [HttpGet("range")]
    public async Task<ActionResult<ForecastRangeDto>> GetRange(CancellationToken ct)
    {
        var range = await manager.GetRangeAsync(ct);
        return Ok(range.ToDto());
    }

    /// <summary>
    /// The map of the forecast page, for example ?today=2026-08-31: every station active in the 28 days up to that
    /// day, with its coordinates and mean departures per active day. The page forecasts the day after "today".
    /// </summary>
    [HttpGet("map")]
    public async Task<ActionResult<StationMapDto>> GetMap([FromQuery, BindRequired] DateOnly today, CancellationToken ct)
    {
        var map = await manager.GetMapAsync(today, ct);
        return Ok(map.ToDto());
    }

    /// <summary>
    /// The forecast of one station for one day, for example ?station=6140.05&amp;day=2026-09-01: the departures of
    /// that day and of the rest of its month, next to the actuals when the day is in the data. The first call after a
    /// quiet period can take 15 s (the serverless endpoint wakes up); 502 when the forecast API fails, 503 when it is
    /// not configured.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ForecastDto>> Get([FromQuery, BindRequired] string station, [FromQuery, BindRequired] DateOnly day, CancellationToken ct)
    {
        var forecast = await manager.ForecastAsync(station, day, ct);
        return Ok(forecast.ToDto());
    }
}
