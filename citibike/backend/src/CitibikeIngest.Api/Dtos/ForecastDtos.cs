namespace CitibikeIngest.Api.Dtos;

public sealed record StationDto(string StationKey, string Name, double? Lat, double? Lng, long TripsAsStart);

/// <summary>Response of GET /api/forecast/range: the days a forecast can be asked for.</summary>
public sealed record ForecastRangeDto(DateOnly FirstDay, DateOnly LastDay, DateOnly Tomorrow, DateOnly? LastWeatherDay);

/// <summary>A station on the map with its mean departures per active day in the window (GET /api/forecast/map).</summary>
public sealed record MapStationDto(string StationKey, string Name, double Lat, double Lng, double MeanDepartures, int ActiveDays);

/// <summary>Response of GET /api/forecast/map?today=...: the window's bounds and every active station.</summary>
public sealed record StationMapDto(DateOnly Today, DateOnly Tomorrow, int WindowDays, IReadOnlyList<MapStationDto> Stations);

public sealed record ModelInfoDto(string Family, string Horizon, double? ValidationMae, double? TestMae);

/// <summary>One horizon: what the model expects, what happened (null for tomorrow), and the model's known error.</summary>
public sealed record HorizonForecastDto(double Prediction, int? Actual, int DaysInHorizon, ModelInfoDto Model);

/// <summary>What the model saw: the history, the calendar and the weather of the station-day, for the page to show.</summary>
public sealed record ForecastInputsDto(
    string Source,
    int HistoryDays,
    int? DepLag1,
    int? DepLag7,
    int? DepLag14,
    int? DepLag28,
    double? DepMean7,
    double? DepMean4Weeks,
    double? DepMtd,
    double? MemberShare28,
    double? EbikeShare28,
    int? ArrLag1,
    double? ArrMean7,
    int? DepRestOfMonthLy,
    bool HasLastYear,
    int IsoDow,
    bool Holiday,
    bool DayOff,
    bool HolidayLag7,
    double TmaxC,
    double TminC,
    double PrcpMm,
    double SnowMm,
    double SnowDepthMm,
    double WindMs,
    DateOnly? WeatherDay);

/// <summary>Response of GET /api/forecast?station=...&amp;day=...</summary>
public sealed record ForecastDto(
    StationDto Station,
    DateOnly Day,
    ForecastInputsDto Inputs,
    HorizonForecastDto DayAhead,
    HorizonForecastDto RestOfMonth);
