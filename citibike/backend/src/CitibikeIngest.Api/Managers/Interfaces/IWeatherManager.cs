using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Managers.Interfaces;

public interface IWeatherManager
{
    /// <summary>
    /// Fetches the daily weather for [from, to] from NOAA and upserts it into citibike.weather_daily, recording the
    /// load in citibike_meta.weather_run. Without dates: from the last stored day minus Weather:RefetchDays (or
    /// Weather:DefaultFrom) up to today. Takes seconds, so it runs inside the request.
    /// Throws <see cref="ConflictException"/> if a load is running and <see cref="UpstreamException"/> if NOAA fails.
    /// </summary>
    Task<WeatherRun> LoadAsync(DateOnly? from, DateOnly? to, string? trigger, CancellationToken ct);

    Task<WeatherRun> GetRunAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<WeatherRun>> ListRunsAsync(int limit, CancellationToken ct);

    /// <summary>Stored days of the configured station in [from, to].</summary>
    Task<WeatherDays> GetDaysAsync(DateOnly from, DateOnly to, CancellationToken ct);
}
