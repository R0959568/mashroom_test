namespace CitibikeIngest.Api.Models;

/// <summary>
/// One day of NOAA daily summaries for one station, in metric units: row in citibike.weather_daily.
/// A null value means the station did not report that variable that day.
/// </summary>
public sealed record WeatherDay(
    string Station,
    DateOnly Day,
    double? TmaxC,
    double? TminC,
    double? PrcpMm,
    double? SnowMm,
    double? SnowDepthMm,
    double? WindMs);

/// <summary>Stored days of one station in a date range: the result of a read.</summary>
public sealed record WeatherDays(string Station, DateOnly From, DateOnly To, IReadOnlyList<WeatherDay> Days);
