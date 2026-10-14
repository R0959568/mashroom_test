using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests.Fakes;

/// <summary>In-memory stand-in for the Postgres repository. Same rules as the real one: one running load, replace per year range.</summary>
public sealed class FakeCalendarRepository : ICalendarRepository
{
    public List<CalendarRun> Runs { get; } = [];

    public List<(string Country, string Subdivision, StoredHoliday Holiday)> Stored { get; } = [];

    public Task<int> MarkStaleRunsFailedAsync(CancellationToken ct)
    {
        var stale = Runs.Where(r => r.Status == RunStatus.Running).ToList();
        stale.ForEach(r => r.Status = RunStatus.Failed);
        return Task.FromResult(stale.Count);
    }

    public Task<CalendarRun?> GetRunningRunAsync(CancellationToken ct) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.Status == RunStatus.Running));

    public Task<CalendarRun> CreateRunAsync(CalendarRun run, CancellationToken ct)
    {
        if (Runs.Any(r => r.Status == RunStatus.Running))
        {
            throw new ConflictException("A calendar load is already in progress");
        }

        run.Id = Runs.Count + 1;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.Status = RunStatus.Running;
        Runs.Add(run);
        return Task.FromResult(run);
    }

    public Task UpdateRunAsync(CalendarRun run, CancellationToken ct) => Task.CompletedTask; // same instance

    public Task<CalendarRun?> GetRunAsync(long id, CancellationToken ct) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));

    public Task<IReadOnlyList<CalendarRun>> ListRunsAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CalendarRun>>(Runs.OrderByDescending(r => r.Id).Take(limit).ToList());

    public Task<int> ReplaceHolidaysAsync(long runId, string country, string subdivision, CalendarRange range,
        HolidayExport export, CancellationToken ct)
    {
        Stored.RemoveAll(s => s.Country == country && s.Subdivision == subdivision
                              && s.Holiday.Holiday.Day >= range.FirstDay && s.Holiday.Holiday.Day <= range.LastDay);
        Stored.AddRange(export.Holidays.Select(h => (country, subdivision, new StoredHoliday(h, export.Source, export.SourceVersion))));
        return Task.FromResult(export.Holidays.Count);
    }

    public Task<IReadOnlyList<StoredHoliday>> GetHolidaysAsync(string country, string subdivision, DateOnly from, DateOnly to,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StoredHoliday>>(Stored
            .Where(s => s.Country == country && s.Subdivision == subdivision
                        && s.Holiday.Holiday.Day >= from && s.Holiday.Holiday.Day <= to)
            .Select(s => s.Holiday)
            .OrderBy(s => s.Holiday.Day)
            .ThenBy(s => s.Holiday.Category)
            .ThenBy(s => s.Holiday.Name, StringComparer.Ordinal)
            .ToList());
}
