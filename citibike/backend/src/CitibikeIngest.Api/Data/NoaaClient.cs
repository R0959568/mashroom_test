using System.Globalization;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Data;

/// <summary>
/// One GET per load: daily-summaries as CSV in metric units. No SDK and no credentials; the same request the
/// EDA notebook made before the data was stored in Postgres.
/// </summary>
public sealed class NoaaClient(HttpClient http, IOptions<WeatherOptions> options) : INoaaClient
{
    /// <summary>Max/min temperature, precipitation, snowfall, snow depth, average wind speed.</summary>
    public const string DataTypes = "TMAX,TMIN,PRCP,SNOW,SNWD,AWND";

    public async Task<IReadOnlyList<WeatherDay>> GetDailyAsync(string station, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var url = options.Value.BaseUrl.TrimEnd('/')
                  + "?dataset=daily-summaries"
                  + "&stations=" + Uri.EscapeDataString(station)
                  + "&startDate=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                  + "&endDate=" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                  + "&dataTypes=" + DataTypes
                  + "&format=csv&units=metric";

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var csv = await response.Content.ReadAsStringAsync(ct);
        return NoaaCsv.Parse(csv);
    }
}
