using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Managers.Interfaces;

public interface IForecastManager
{
    /// <summary>Stations to pick from, busiest first; an empty query lists the busiest.</summary>
    Task<IReadOnlyList<StationSummary>> ListStationsAsync(string? query, int limit, CancellationToken ct);

    /// <summary>The days a forecast can be asked for: the data's range plus the day after it.</summary>
    Task<DataRange> GetRangeAsync(CancellationToken ct);

    /// <summary>
    /// The map of the forecast page: every station active in the 28 days up to <paramref name="today"/> with its mean
    /// departures per active day. Throws <see cref="ValidationException"/> when today is outside the data.
    /// </summary>
    Task<StationMap> GetMapAsync(DateOnly today, CancellationToken ct);

    /// <summary>
    /// The forecast of one station for one day: the departures of that day and of the rest of its month, from the
    /// models behind the forecast API, next to the actuals when the day is in the data. Days inside the data use the
    /// feature table; the day after the data is computed from the trips up to the last day.
    /// Throws <see cref="NotFoundException"/> for an unknown station, <see cref="ValidationException"/> for a day
    /// outside the range or a station without 28 days of history, <see cref="UpstreamException"/> when the API fails.
    /// </summary>
    Task<Forecast> ForecastAsync(string stationKey, DateOnly day, CancellationToken ct);
}
