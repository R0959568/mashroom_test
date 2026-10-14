namespace CitibikeIngest.Api.Models;

/// <summary>A station the user can pick: row of citibike_prep.station_dim.</summary>
public sealed record StationSummary(string StationKey, string Name, double? Lat, double? Lng, long TripsAsStart);

/// <summary>
/// What the data allows: the feature table covers [FirstDay, LastDay]; the day after LastDay is "tomorrow", the one
/// day the features can be computed for from the trips up to LastDay. The weather may run further than the trips.
/// </summary>
public sealed record DataRange(DateOnly FirstDay, DateOnly LastDay, DateOnly? LastWeatherDay)
{
    public DateOnly Tomorrow => LastDay.AddDays(1);
}

/// <summary>A station on the map: its mean departures per active day in the window that ends on the chosen day.</summary>
public sealed record MapStation(string StationKey, string Name, double Lat, double Lng, double MeanDepartures, int ActiveDays);

/// <summary>
/// Every station with trips in the window ending on Today, for the map of the forecast page. The page treats Today as
/// "the last observed day" and asks the forecast for Tomorrow; the window is the model's own 28 days.
/// </summary>
public sealed record StationMap(DateOnly Today, int WindowDays, IReadOnlyList<MapStation> Stations)
{
    public DateOnly Tomorrow => Today.AddDays(1);
}

/// <summary>
/// The feature row of one station-day, as 05 §4 defines it: read from citibike_prep.station_day_features for a day
/// inside the table, computed from station_day, calendar_day and weather_daily for the day after it. Null means the
/// value does not exist (not enough history, no year earlier); the manager decides what that means per horizon.
/// </summary>
public sealed record FeatureRow
{
    public required string StationKey { get; init; }
    public required DateOnly Day { get; init; }
    public required FeatureSource Source { get; init; }
    public required int HistoryDays { get; init; }
    public required int DaysInHorizon { get; init; }

    // the targets, known for a day inside the table only
    public int? ActualDay { get; init; }
    public int? ActualRestOfMonth { get; init; }

    // history
    public int? DepLag1 { get; init; }
    public int? DepLag7 { get; init; }
    public int? DepLag14 { get; init; }
    public int? DepLag28 { get; init; }
    public double? DepMean7 { get; init; }
    public double? DepMean4Weeks { get; init; }
    public double? DepMtd { get; init; }
    public int? DepSum28 { get; init; }
    public double? MemberShare28 { get; init; }
    public double? EbikeShare28 { get; init; }
    public int? ArrLag1 { get; init; }
    public double? ArrMean7 { get; init; }
    public int? DepRestOfMonthLy { get; init; }
    public int? DepSum28Ly { get; init; }

    // calendar
    public required int IsoDow { get; init; }
    public required int Month { get; init; }
    public required int DayOfMonth { get; init; }
    public required bool Holiday { get; init; }
    public required bool DayOff { get; init; }
    public required bool HolidayLag7 { get; init; }

    // weather of the day (NOAA Central Park; for tomorrow the last observed values, WeatherDay says of which day)
    public required double TmaxC { get; init; }
    public required double TminC { get; init; }
    public required double PrcpMm { get; init; }
    public required double SnowMm { get; init; }
    public required double SnowDepthMm { get; init; }
    public required double WindMs { get; init; }
    public DateOnly? WeatherDay { get; init; }
}

public enum FeatureSource
{
    /// <summary>The row exists in the feature table: the day is inside the data, the actuals are known.</summary>
    Table,

    /// <summary>The day after the last day of the data: computed from the trips up to yesterday; no actuals.</summary>
    Computed,
}

public enum Horizon
{
    Day,
    RestOfMonth,
}

/// <summary>What the model behind the URL says about itself (part of every answer of the forecast API).</summary>
public sealed record ModelInfo(string Family, string Horizon, double? ValidationMae, double? TestMae);

/// <summary>One horizon's answer: the departures the model expects, the actual when the day is in the data.</summary>
public sealed record HorizonForecast(Horizon Horizon, double Prediction, int? Actual, int DaysInHorizon, ModelInfo Model);

public sealed record Forecast(
    StationSummary Station,
    FeatureRow Inputs,
    HorizonForecast Day,
    HorizonForecast RestOfMonth);

/// <summary>The forecast API's answer to one request: as many predictions as rows were sent.</summary>
public sealed record ForecastResponse(IReadOnlyList<double> Predictions, ModelInfo Model);
