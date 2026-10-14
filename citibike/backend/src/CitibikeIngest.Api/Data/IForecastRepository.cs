using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Data;

public interface IForecastRepository
{
    /// <summary>Stations by name (case-insensitive contains) or exact key, busiest first.</summary>
    Task<IReadOnlyList<StationSummary>> ListStationsAsync(string query, int limit, CancellationToken ct);

    Task<StationSummary?> GetStationAsync(string stationKey, CancellationToken ct);

    /// <summary>First and last day of the feature table, and the last day the weather is known for.</summary>
    Task<DataRange> GetRangeAsync(string weatherStation, CancellationToken ct);

    /// <summary>
    /// Every station with trips in the <paramref name="windowDays"/> days up to and including <paramref name="today"/>,
    /// with its mean departures per active day and the number of active days; stations without a name or coordinates
    /// are left out. Busiest first.
    /// </summary>
    Task<IReadOnlyList<MapStation>> ListMapStationsAsync(DateOnly today, int windowDays, CancellationToken ct);

    /// <summary>The feature row of a day inside the table, or null when the station has no row for that day.</summary>
    Task<FeatureRow?> GetTableRowAsync(string stationKey, DateOnly day, CancellationToken ct);

    /// <summary>
    /// The feature row of a day after the table, computed from the trips before it (the window definitions of 05 §4
    /// on citibike_prep.station_day), the calendar and the last observed weather. Null when the station was not
    /// active on the day before.
    /// </summary>
    Task<FeatureRow?> ComputeRowAsync(string stationKey, DateOnly day, string weatherStation, CancellationToken ct);
}
