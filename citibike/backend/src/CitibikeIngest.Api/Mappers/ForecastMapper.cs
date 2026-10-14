using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Managers;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Mappers;

public static class ForecastMapper
{
    public static StationDto ToDto(this StationSummary station) =>
        new(station.StationKey, station.Name, station.Lat, station.Lng, station.TripsAsStart);

    public static ForecastRangeDto ToDto(this DataRange range) =>
        new(range.FirstDay, range.LastDay, range.Tomorrow, range.LastWeatherDay);

    public static MapStationDto ToDto(this MapStation station) =>
        new(station.StationKey, station.Name, station.Lat, station.Lng, Math.Round(station.MeanDepartures, 1), station.ActiveDays);

    public static StationMapDto ToDto(this StationMap map) =>
        new(map.Today, map.Tomorrow, map.WindowDays, map.Stations.Select(ToDto).ToList());

    public static ModelInfoDto ToDto(this ModelInfo model) =>
        new(model.Family, model.Horizon, model.ValidationMae, model.TestMae);

    public static HorizonForecastDto ToDto(this HorizonForecast horizon) =>
        new(Math.Round(horizon.Prediction, 1), horizon.Actual, horizon.DaysInHorizon, horizon.Model.ToDto());

    public static ForecastInputsDto ToDto(this FeatureRow row) => new(
        row.Source == FeatureSource.Table ? "history" : "tomorrow",
        row.HistoryDays,
        row.DepLag1,
        row.DepLag7,
        row.DepLag14,
        row.DepLag28,
        Round(row.DepMean7),
        Round(row.DepMean4Weeks),
        Round(row.DepMtd),
        Round(row.MemberShare28, 3),
        Round(row.EbikeShare28, 3),
        row.ArrLag1,
        Round(row.ArrMean7),
        row.DepRestOfMonthLy,
        ModelInputs.LastYearRate(row) is not null,
        row.IsoDow,
        row.Holiday,
        row.DayOff,
        row.HolidayLag7,
        row.TmaxC,
        row.TminC,
        row.PrcpMm,
        row.SnowMm,
        row.SnowDepthMm,
        row.WindMs,
        row.WeatherDay);

    public static ForecastDto ToDto(this Forecast forecast) => new(
        forecast.Station.ToDto(),
        forecast.Inputs.Day,
        forecast.Inputs.ToDto(),
        forecast.Day.ToDto(),
        forecast.RestOfMonth.ToDto());

    private static double? Round(double? value, int digits = 1) => value is { } v ? Math.Round(v, digits) : null;
}
