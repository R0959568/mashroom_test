using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Data;

/// <summary>Reads daily weather summaries from the public NOAA NCEI Access Data Service.</summary>
public interface INoaaClient
{
    /// <summary>All days NOAA has for the station in [from, to]; days not published yet are simply absent.</summary>
    Task<IReadOnlyList<WeatherDay>> GetDailyAsync(string station, DateOnly from, DateOnly to, CancellationToken ct);
}
