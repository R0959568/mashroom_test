namespace CitibikeIngest.Api.Dtos;

/// <summary>
/// Body of POST /api/weather/runs. All fields optional: without from the load starts Weather:RefetchDays before the
/// last stored day (or at Weather:DefaultFrom on the first load); without to it runs up to today; trigger defaults to "manual".
/// </summary>
public sealed record StartWeatherRunRequestDto(DateOnly? From, DateOnly? To, string? Trigger);

public sealed record WeatherRunDto(
    long Id,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Station,
    DateOnly From,
    DateOnly To,
    string RangeSource,
    string Trigger,
    int DaysReceived,
    int DaysWritten,
    string? Error);

/// <summary>One day in metric units: temperatures in °C, precipitation, snowfall and snow depth in mm, wind in m/s.</summary>
public sealed record WeatherDayDto(
    DateOnly Day,
    double? TmaxC,
    double? TminC,
    double? PrcpMm,
    double? SnowMm,
    double? SnowDepthMm,
    double? WindMs);

/// <summary>Response of GET /api/weather/days.</summary>
public sealed record WeatherDaysDto(string Station, DateOnly From, DateOnly To, int Count, IReadOnlyList<WeatherDayDto> Days);
