using System.Globalization;

namespace CitibikeIngest.Api.Options;

/// <summary>
/// Settings for the NOAA weather load. Bound from the "Weather" section; in the container every value is a
/// Weather__Xxx environment variable. The service is public: no account or key.
/// </summary>
public sealed class WeatherOptions
{
    public const string Section = "Weather";

    /// <summary>NCEI Access Data Service, dataset daily-summaries (GHCN-Daily).</summary>
    public string BaseUrl { get; set; } = "https://www.ncei.noaa.gov/access/services/data/v1";

    /// <summary>GHCN station id. Default: New York Central Park.</summary>
    public string Station { get; set; } = "USW00094728";

    /// <summary>First day of the very first load (ISO date); the trip data starts in 2021.</summary>
    public string DefaultFrom { get; set; } = "2021-01-01";

    /// <summary>A load without a start date begins this many days before the last stored day: NOAA corrects recent days.</summary>
    public int RefetchDays { get; set; } = 30;

    /// <summary>Upper limit for one load or one read, to keep a request well within the request timeout.</summary>
    public int MaxDaysPerRequest { get; set; } = 3700;

    public DateOnly DefaultFromDate => DateOnly.ParseExact(DefaultFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
