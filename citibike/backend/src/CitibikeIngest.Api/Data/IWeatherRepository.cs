using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Data;

/// <summary>Reads and writes citibike.weather_daily and the load history in citibike_meta.weather_run.</summary>
public interface IWeatherRepository
{
    /// <summary>Marks loads left in "running" by a crashed or restarted backend as failed. Returns how many.</summary>
    Task<int> MarkStaleRunsFailedAsync(CancellationToken ct);

    Task<WeatherRun?> GetRunningRunAsync(CancellationToken ct);

    /// <summary>Inserts the run and fills in Id and StartedAt. Throws <see cref="ConflictException"/> when a load is already running.</summary>
    Task<WeatherRun> CreateRunAsync(WeatherRun run, CancellationToken ct);

    Task UpdateRunAsync(WeatherRun run, CancellationToken ct);

    Task<WeatherRun?> GetRunAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<WeatherRun>> ListRunsAsync(int limit, CancellationToken ct);

    /// <summary>The most recent stored day for the station, or null when nothing is stored yet.</summary>
    Task<DateOnly?> GetLastDayAsync(string station, CancellationToken ct);

    /// <summary>Inserts new days and overwrites existing ones (NOAA corrects recent days). Returns the rows written.</summary>
    Task<int> UpsertDaysAsync(long runId, IReadOnlyList<WeatherDay> days, CancellationToken ct);

    Task<IReadOnlyList<WeatherDay>> GetDaysAsync(string station, DateOnly from, DateOnly to, CancellationToken ct);
}
