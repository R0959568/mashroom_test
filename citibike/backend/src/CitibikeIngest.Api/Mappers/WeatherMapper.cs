using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Mappers;

public static class WeatherMapper
{
    public static WeatherRunDto ToDto(this WeatherRun run) => new(
        run.Id,
        run.Status.ToApi(),
        run.StartedAt,
        run.FinishedAt,
        run.Station,
        run.From,
        run.To,
        run.RangeSource.ToApi(),
        run.Trigger,
        run.DaysReceived,
        run.DaysWritten,
        run.Error);

    /// <summary>The station is the same for every day of a response, so it moves to the envelope.</summary>
    public static WeatherDayDto ToDto(this WeatherDay day) =>
        new(day.Day, day.TmaxC, day.TminC, day.PrcpMm, day.SnowMm, day.SnowDepthMm, day.WindMs);

    public static WeatherDaysDto ToDto(this WeatherDays result) =>
        new(result.Station, result.From, result.To, result.Days.Count, result.Days.Select(ToDto).ToList());
}
