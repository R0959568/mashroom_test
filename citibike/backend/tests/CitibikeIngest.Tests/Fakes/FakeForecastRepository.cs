using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests.Fakes;

/// <summary>In-memory stand-in: stations by key, feature rows by (station, day), and a computed row per station for "tomorrow".</summary>
public sealed class FakeForecastRepository : IForecastRepository
{
    public Dictionary<string, StationSummary> Stations { get; } = [];

    public Dictionary<(string StationKey, DateOnly Day), FeatureRow> TableRows { get; } = [];

    public Dictionary<string, FeatureRow> ComputedRows { get; } = [];

    public DataRange Range { get; set; } = new(new DateOnly(2021, 1, 1), new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 29));

    public List<(string StationKey, DateOnly Day)> ComputeCalls { get; } = [];

    public List<MapStation> MapStations { get; } = [];

    public List<(DateOnly Today, int WindowDays)> MapCalls { get; } = [];

    public Task<IReadOnlyList<MapStation>> ListMapStationsAsync(DateOnly today, int windowDays, CancellationToken ct)
    {
        MapCalls.Add((today, windowDays));
        return Task.FromResult<IReadOnlyList<MapStation>>(MapStations);
    }

    public Task<IReadOnlyList<StationSummary>> ListStationsAsync(string query, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StationSummary>>(Stations.Values
            .Where(s => query == "" || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.StationKey == query)
            .OrderByDescending(s => s.TripsAsStart)
            .Take(limit)
            .ToList());

    public Task<StationSummary?> GetStationAsync(string stationKey, CancellationToken ct) =>
        Task.FromResult(Stations.GetValueOrDefault(stationKey));

    public Task<DataRange> GetRangeAsync(string weatherStation, CancellationToken ct) => Task.FromResult(Range);

    public Task<FeatureRow?> GetTableRowAsync(string stationKey, DateOnly day, CancellationToken ct) =>
        Task.FromResult(TableRows.GetValueOrDefault((stationKey, day)));

    public Task<FeatureRow?> ComputeRowAsync(string stationKey, DateOnly day, string weatherStation, CancellationToken ct)
    {
        ComputeCalls.Add((stationKey, day));
        return Task.FromResult(ComputedRows.GetValueOrDefault(stationKey));
    }
}
