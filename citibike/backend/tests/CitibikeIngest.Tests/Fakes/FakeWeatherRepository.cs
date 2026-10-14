using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests.Fakes;

/// <summary>In-memory stand-in for the Postgres repository. Same rules as the real one: one running load, upsert per (station, day).</summary>
public sealed class FakeWeatherRepository : IWeatherRepository
{
    public List<WeatherRun> Runs { get; } = [];

    public Dictionary<(string Station, DateOnly Day), WeatherDay> Stored { get; } = [];

    public Task<int> MarkStaleRunsFailedAsync(CancellationToken ct)
    {
        var stale = Runs.Where(r => r.Status == RunStatus.Running).ToList();
        stale.ForEach(r => r.Status = RunStatus.Failed);
        return Task.FromResult(stale.Count);
    }

    public Task<WeatherRun?> GetRunningRunAsync(CancellationToken ct) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.Status == RunStatus.Running));

    public Task<WeatherRun> CreateRunAsync(WeatherRun run, CancellationToken ct)
    {
        if (Runs.Any(r => r.Status == RunStatus.Running))
        {
            throw new ConflictException("A weather load is already in progress");
        }

        run.Id = Runs.Count + 1;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.Status = RunStatus.Running;
        Runs.Add(run);
        return Task.FromResult(run);
    }

    public Task UpdateRunAsync(WeatherRun run, CancellationToken ct) => Task.CompletedTask; // same instance

    public Task<WeatherRun?> GetRunAsync(long id, CancellationToken ct) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));

    public Task<IReadOnlyList<WeatherRun>> ListRunsAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WeatherRun>>(Runs.OrderByDescending(r => r.Id).Take(limit).ToList());

    public Task<DateOnly?> GetLastDayAsync(string station, CancellationToken ct) =>
        Task.FromResult(Stored.Keys.Where(k => k.Station == station).Select(k => (DateOnly?)k.Day).Max());

    public Task<int> UpsertDaysAsync(long runId, IReadOnlyList<WeatherDay> days, CancellationToken ct)
    {
        foreach (var day in days)
        {
            Stored[(day.Station, day.Day)] = day;
        }

        return Task.FromResult(days.Count);
    }

    public Task<IReadOnlyList<WeatherDay>> GetDaysAsync(string station, DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WeatherDay>>(Stored.Values
            .Where(d => d.Station == station && d.Day >= from && d.Day <= to)
            .OrderBy(d => d.Day)
            .ToList());
}
