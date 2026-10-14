using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Data;

/// <summary>The forecast API of notebook 11: one request per horizon, as many rows as station-days.</summary>
public interface IForecastClient
{
    /// <summary>
    /// Sends the model inputs of the rows (the names of the feature table, the station key as text, the scale) and
    /// returns the departures the model expects, one per row, with the model's description.
    /// Throws <see cref="UpstreamException"/> when the API fails twice (a cold endpoint gets one second try).
    /// </summary>
    Task<ForecastResponse> PredictAsync(Horizon horizon, IReadOnlyList<IReadOnlyDictionary<string, object>> rows, CancellationToken ct);
}
