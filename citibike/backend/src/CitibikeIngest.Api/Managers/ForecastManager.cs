using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Managers;

public sealed class ForecastManager(
    IForecastRepository repository,
    IForecastClient client,
    IOptions<ForecastOptions> options,
    ILogger<ForecastManager> logger) : IForecastManager
{
    public async Task<IReadOnlyList<StationSummary>> ListStationsAsync(string? query, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
        {
            throw new ValidationException("limit must be between 1 and 100");
        }

        return await repository.ListStationsAsync((query ?? "").Trim(), limit, ct);
    }

    public Task<DataRange> GetRangeAsync(CancellationToken ct) => repository.GetRangeAsync(options.Value.WeatherStation, ct);

    /// <summary>The window of the map: the model's own 28 days (the lags and sums of 05 §4), so the map shows what the model sees.</summary>
    public const int MapWindowDays = 28;

    public async Task<StationMap> GetMapAsync(DateOnly today, CancellationToken ct)
    {
        var range = await repository.GetRangeAsync(options.Value.WeatherStation, ct);
        if (today < range.FirstDay || today > range.LastDay)
        {
            throw new ValidationException($"today must be between {range.FirstDay:yyyy-MM-dd} and {range.LastDay:yyyy-MM-dd}, the days of the data; the forecast is for the day after it");
        }

        return new StationMap(today, MapWindowDays, await repository.ListMapStationsAsync(today, MapWindowDays, ct));
    }

    public async Task<Forecast> ForecastAsync(string stationKey, DateOnly day, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            throw new NotConfiguredException("The forecast API is not configured (Forecast:Url and Forecast:ApiKey)");
        }

        var station = await repository.GetStationAsync(stationKey, ct)
                      ?? throw new NotFoundException($"Station {stationKey} does not exist");
        var range = await repository.GetRangeAsync(settings.WeatherStation, ct);
        if (day < range.FirstDay || day > range.Tomorrow)
        {
            throw new ValidationException($"day must be between {range.FirstDay:yyyy-MM-dd} and {range.Tomorrow:yyyy-MM-dd} (the data ends on {range.LastDay:yyyy-MM-dd}; the day after it is computed from the trips up to then)");
        }

        var row = day <= range.LastDay
            ? await repository.GetTableRowAsync(stationKey, day, ct)
            : await repository.ComputeRowAsync(stationKey, day, settings.WeatherStation, ct);
        if (row is null)
        {
            throw new ValidationException($"Station {stationKey} was not active on {day:yyyy-MM-dd}");
        }

        if (row.HistoryDays < settings.MinHistoryDays)
        {
            throw new ValidationException(
                $"Station {stationKey} had {row.HistoryDays} day(s) of history on {day:yyyy-MM-dd}; a forecast needs {settings.MinHistoryDays} (the cold-start rule of 05)");
        }

        var inputs = ModelInputs.Build(row);
        var dayAnswer = await client.PredictAsync(Horizon.Day, [inputs.Day], ct);
        var restAnswer = await client.PredictAsync(Horizon.RestOfMonth, [inputs.RestOfMonth], ct);
        logger.LogInformation("Forecast {Station} {Day:yyyy-MM-dd} ({Source}): day {DayPrediction:F1} (actual {DayActual}), rest of month {RestPrediction:F0} (actual {RestActual})",
            stationKey, day, row.Source, dayAnswer.Predictions[0], row.ActualDay, restAnswer.Predictions[0], row.ActualRestOfMonth);
        return new Forecast(
            station,
            row,
            new HorizonForecast(Horizon.Day, dayAnswer.Predictions[0], row.ActualDay, 1, dayAnswer.Model),
            new HorizonForecast(Horizon.RestOfMonth, restAnswer.Predictions[0], row.ActualRestOfMonth, row.DaysInHorizon, restAnswer.Model));
    }
}

/// <summary>
/// The model inputs of one feature row, per horizon, exactly as modelling.design() and model_inputs() of the notebooks
/// build them (05 §7, 07 §2): flags as 0/1, the shares of a station without departures as 0, the rest-of-month rates
/// with their fallbacks and the fallback flag, the station key as text, and the scale (1 for the day, the days left in
/// the month for the rest of the month: the model predicts departures per remaining day). The keys are the column
/// names the models were trained on; the forecast API picks the ones each model needs.
/// </summary>
public static class ModelInputs
{
    public static (IReadOnlyDictionary<string, object> Day, IReadOnlyDictionary<string, object> RestOfMonth) Build(FeatureRow row)
    {
        var common = new Dictionary<string, object>
        {
            ["station_key"] = row.StationKey,
            ["dep_lag_1"] = Required(row.DepLag1, "dep_lag_1"),
            ["dep_lag_7"] = Required(row.DepLag7, "dep_lag_7"),
            ["dep_lag_14"] = Required(row.DepLag14, "dep_lag_14"),
            ["dep_lag_28"] = Required(row.DepLag28, "dep_lag_28"),
            ["dep_mean_7"] = Required(row.DepMean7, "dep_mean_7"),
            ["dep_mean_4_weeks"] = Required(row.DepMean4Weeks, "dep_mean_4_weeks"),
            ["arr_lag_1"] = Required(row.ArrLag1, "arr_lag_1"),
            ["arr_mean_7"] = Required(row.ArrMean7, "arr_mean_7"),
            ["member_share_28"] = row.MemberShare28 ?? 0.0,
            ["ebike_share_28"] = row.EbikeShare28 ?? 0.0,
            ["history_days"] = row.HistoryDays,
            ["holiday"] = row.Holiday ? 1 : 0,
            ["day_off"] = row.DayOff ? 1 : 0,
            ["holiday_lag_7"] = row.HolidayLag7 ? 1 : 0,
            ["iso_dow"] = row.IsoDow,
            ["month"] = row.Month,
        };

        var day = new Dictionary<string, object>(common)
        {
            ["tmax_c"] = row.TmaxC,
            ["tmin_c"] = row.TminC,
            ["prcp_mm"] = row.PrcpMm,
            ["snow_mm"] = row.SnowMm,
            ["snow_depth_mm"] = row.SnowDepthMm,
            ["wind_ms"] = row.WindMs,
            ["scale"] = 1.0,
        };

        var depMean7 = (double)common["dep_mean_7"];
        var lastYearRate = LastYearRate(row);
        var rest = new Dictionary<string, object>(common)
        {
            ["days_in_horizon"] = row.DaysInHorizon,
            ["day_of_month"] = row.DayOfMonth,
            ["mtd_rate"] = row.DepMtd ?? depMean7,
            ["last_year_rate"] = lastYearRate ?? depMean7,
            ["has_last_year"] = lastYearRate is null ? 0 : 1,
            ["scale"] = (double)row.DaysInHorizon,
        };
        return (day, rest);
    }

    /// <summary>
    /// Last year's rest of the month scaled by the station's growth (the last 28 days over the same 28 days a year
    /// earlier), per remaining day; null where any part is missing or the ratio is not finite (a station that was
    /// almost empty a year earlier), as the notebooks do with np.isfinite.
    /// </summary>
    public static double? LastYearRate(FeatureRow row)
    {
        if (row.DepRestOfMonthLy is not { } restLy || row.DepSum28 is not { } sum28 || row.DepSum28Ly is not { } sum28Ly || row.DaysInHorizon <= 0)
        {
            return null;
        }

        var rate = restLy * ((double)sum28 / sum28Ly) / row.DaysInHorizon;
        return double.IsFinite(rate) ? rate : null;
    }

    private static double Required(double? value, string name) =>
        value ?? throw new ValidationException($"Not enough history for the model: {name} is missing");

    private static int Required(int? value, string name) =>
        value ?? throw new ValidationException($"Not enough history for the model: {name} is missing");
}
