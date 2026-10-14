using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Data;

/// <summary>Reads and writes citibike.holiday and the load history in citibike_meta.calendar_run.</summary>
public interface ICalendarRepository
{
    /// <summary>Marks loads left in "running" by a crashed or restarted backend as failed. Returns how many.</summary>
    Task<int> MarkStaleRunsFailedAsync(CancellationToken ct);

    Task<CalendarRun?> GetRunningRunAsync(CancellationToken ct);

    /// <summary>Inserts the run and fills in Id and StartedAt. Throws <see cref="ConflictException"/> when a load is already running.</summary>
    Task<CalendarRun> CreateRunAsync(CalendarRun run, CancellationToken ct);

    Task UpdateRunAsync(CalendarRun run, CancellationToken ct);

    Task<CalendarRun?> GetRunAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<CalendarRun>> ListRunsAsync(int limit, CancellationToken ct);

    /// <summary>
    /// In one transaction: deletes the stored holidays of the country/subdivision in [range.FirstDay, range.LastDay]
    /// and inserts <paramref name="export"/>. A replace instead of an upsert, so a holiday that a newer package
    /// version renames or drops does not linger. Returns the rows inserted.
    /// </summary>
    Task<int> ReplaceHolidaysAsync(long runId, string country, string subdivision, CalendarRange range,
        HolidayExport export, CancellationToken ct);

    Task<IReadOnlyList<StoredHoliday>> GetHolidaysAsync(string country, string subdivision, DateOnly from, DateOnly to,
        CancellationToken ct);
}
