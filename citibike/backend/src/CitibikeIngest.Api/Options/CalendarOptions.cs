namespace CitibikeIngest.Api.Options;

/// <summary>
/// Settings for the holiday calendar load. Bound from the "Calendar" section; in the container every value is a
/// Calendar__Xxx environment variable. The Python executable is shared with the converter (Ingest:PythonExecutable).
/// </summary>
public sealed class CalendarOptions
{
    public const string Section = "Calendar";

    /// <summary>Script that runs the pinned `holidays` package and prints the result as JSON.</summary>
    public string ExporterScript { get; set; } = "/app/ingest/export_holidays.py";

    /// <summary>ISO 3166 country code passed to the package.</summary>
    public string Country { get; set; } = "US";

    /// <summary>State code: NY adds the New York State holidays (Election Day, Lincoln's Birthday, ...). Empty = country-wide only.</summary>
    public string Subdivision { get; set; } = "NY";

    /// <summary>
    /// Comma-separated package categories. public = federal + state holidays; unofficial = observances without a
    /// day off that still change how people ride (Easter Sunday, Good Friday, Christmas Eve, New Year's Eve, ...).
    /// </summary>
    public string Categories { get; set; } = "public,unofficial";

    /// <summary>First year of a load without fromYear; the trip data starts in 2021.</summary>
    public int DefaultFromYear { get; set; } = 2021;

    /// <summary>A load without toYear runs this many years past the current one, so forecasts can join future dates.</summary>
    public int YearsAhead { get; set; } = 2;

    /// <summary>Upper limit for one load, in years.</summary>
    public int MaxYearsPerRequest { get; set; } = 50;

    public IReadOnlyList<string> CategoryList => Categories
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.ToLowerInvariant())
        .Distinct()
        .ToList();
}
