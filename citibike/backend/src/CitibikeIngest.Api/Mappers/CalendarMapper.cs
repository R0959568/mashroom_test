using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Mappers;

public static class CalendarMapper
{
    public static CalendarRunDto ToDto(this CalendarRun run) => new(
        run.Id,
        run.Status.ToApi(),
        run.StartedAt,
        run.FinishedAt,
        run.Country,
        run.Subdivision,
        run.Categories,
        run.FromYear,
        run.ToYear,
        run.RangeSource.ToApi(),
        run.Trigger,
        run.Source,
        run.SourceVersion,
        run.HolidaysReceived,
        run.HolidaysWritten,
        run.Error);

    public static HolidayDto ToDto(this StoredHoliday stored) => new(
        stored.Holiday.Day,
        stored.Holiday.Name,
        stored.Holiday.Category,
        stored.Holiday.Observed,
        stored.Source,
        stored.SourceVersion);

    /// <summary>Country and subdivision are the same for every row of a response, so they move to the envelope.</summary>
    public static HolidaysDto ToDto(this HolidayDays result) =>
        new(result.Country, result.Subdivision, result.From, result.To, result.Holidays.Count, result.Holidays.Select(ToDto).ToList());
}
