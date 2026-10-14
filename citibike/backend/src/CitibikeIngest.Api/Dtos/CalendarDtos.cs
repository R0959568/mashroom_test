namespace CitibikeIngest.Api.Dtos;

/// <summary>
/// Body of POST /api/calendar/runs. All fields optional: without fromYear the load starts at Calendar:DefaultFromYear;
/// without toYear it runs to the current year plus Calendar:YearsAhead; trigger defaults to "manual".
/// </summary>
public sealed record StartCalendarRunRequestDto(int? FromYear, int? ToYear, string? Trigger);

public sealed record CalendarRunDto(
    long Id,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Country,
    string Subdivision,
    string Categories,
    int FromYear,
    int ToYear,
    string RangeSource,
    string Trigger,
    string? Source,
    string? SourceVersion,
    int HolidaysReceived,
    int HolidaysWritten,
    string? Error);

/// <summary>One holiday as the package reports it; observed = the day off for a holiday that falls on a weekend.</summary>
public sealed record HolidayDto(DateOnly Day, string Name, string Category, bool Observed, string Source, string SourceVersion);

/// <summary>Response of GET /api/calendar/holidays.</summary>
public sealed record HolidaysDto(string Country, string Subdivision, DateOnly From, DateOnly To, int Count, IReadOnlyList<HolidayDto> Holidays);
