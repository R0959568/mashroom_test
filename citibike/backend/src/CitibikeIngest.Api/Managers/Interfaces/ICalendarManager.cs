using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Managers.Interfaces;

public interface ICalendarManager
{
    /// <summary>
    /// Exports the holidays of [fromYear, toYear] from the pinned `holidays` package and replaces those years in
    /// citibike.holiday, recording the load in citibike_meta.calendar_run. Without years: Calendar:DefaultFromYear up
    /// to the current year plus Calendar:YearsAhead. Takes about a second, so it runs inside the request.
    /// Throws <see cref="ConflictException"/> if a load is running, <see cref="ValidationException"/> for bad years and
    /// <see cref="UpstreamException"/> if the export fails or returns nothing usable (the stored holidays are then left as they were).
    /// </summary>
    Task<CalendarRun> LoadAsync(int? fromYear, int? toYear, string? trigger, CancellationToken ct);

    Task<CalendarRun> GetRunAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<CalendarRun>> ListRunsAsync(int limit, CancellationToken ct);

    /// <summary>Stored holidays of the configured country/subdivision in [from, to].</summary>
    Task<HolidayDays> GetHolidaysAsync(DateOnly from, DateOnly to, CancellationToken ct);
}
