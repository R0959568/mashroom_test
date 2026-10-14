using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests.Fakes;

/// <summary>Records every request and answers with a fixed prediction per horizon, or throws what the test queues.</summary>
public sealed class FakeForecastClient : IForecastClient
{
    public List<(Horizon Horizon, IReadOnlyDictionary<string, object> Row)> Calls { get; } = [];

    public Dictionary<Horizon, double> Predictions { get; } = new() { [Horizon.Day] = 12.3, [Horizon.RestOfMonth] = 345.6 };

    public Exception? Failure { get; set; }

    public Task<ForecastResponse> PredictAsync(Horizon horizon, IReadOnlyList<IReadOnlyDictionary<string, object>> rows, CancellationToken ct)
    {
        Calls.Add((horizon, rows.Single()));
        if (Failure is { } failure)
        {
            throw failure;
        }

        var model = new ModelInfo(horizon == Horizon.Day ? "lightgbm" : "extra_trees", horizon == Horizon.Day ? "day" : "rest_of_month", 9.634, 9.8);
        return Task.FromResult(new ForecastResponse([Predictions[horizon]], model));
    }
}
