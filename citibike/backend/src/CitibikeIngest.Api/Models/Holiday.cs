namespace CitibikeIngest.Api.Models;

/// <summary>
/// One holiday as the `holidays` package reports it, unchanged: row in citibike.holiday. A holiday on a weekend
/// gives two rows, the actual date (Observed = false) and the day off (Observed = true, name ends in "(observed)").
/// </summary>
public sealed record Holiday(DateOnly Day, string Name, string Category, bool Observed);

/// <summary>What one exporter call returned: the rows plus the package that produced them.</summary>
public sealed record HolidayExport(string Source, string SourceVersion, IReadOnlyList<Holiday> Holidays);

/// <summary>Stored holidays of one country/subdivision in a date range: the result of a read.</summary>
public sealed record StoredHoliday(Holiday Holiday, string Source, string SourceVersion);

public sealed record HolidayDays(string Country, string Subdivision, DateOnly From, DateOnly To, IReadOnlyList<StoredHoliday> Holidays);
