using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests.Fakes;

/// <summary>Returns the days in <see cref="Days"/> that fall in the requested range, or fails like an unreachable NOAA.</summary>
public sealed class FakeNoaaClient : INoaaClient
{
    public List<WeatherDay> Days { get; } = [];

    public List<(string Station, DateOnly From, DateOnly To)> Calls { get; } = [];

    public Exception? FailWith { get; set; }

    public Task<IReadOnlyList<WeatherDay>> GetDailyAsync(string station, DateOnly from, DateOnly to, CancellationToken ct)
    {
        Calls.Add((station, from, to));
        if (FailWith is not null)
        {
            throw FailWith;
        }

        return Task.FromResult<IReadOnlyList<WeatherDay>>(
            Days.Where(d => d.Station == station && d.Day >= from && d.Day <= to).ToList());
    }
}
