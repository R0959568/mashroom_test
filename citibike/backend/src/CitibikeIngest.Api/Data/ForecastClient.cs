using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Data;

/// <summary>
/// HTTP client of the forecast API (API Gateway → Lambda → SageMaker endpoint, notebook 11). The key travels in the
/// x-api-key header; the body is {"horizon": ..., "rows": [...]}, the answer {"predictions": [...], "model": {...}}.
/// A serverless endpoint that slept answers the first call in about 15 s or, past the gateway's limit, with a 502 or
/// 504: one second try after a short pause covers that; the second failure is the caller's.
/// </summary>
public sealed class ForecastClient(HttpClient http, IOptions<ForecastOptions> options, ILogger<ForecastClient> logger) : IForecastClient
{
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ForecastResponse> PredictAsync(Horizon horizon, IReadOnlyList<IReadOnlyDictionary<string, object>> rows, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            throw new NotConfiguredException("The forecast API is not configured (Forecast:Url and Forecast:ApiKey)");
        }

        var body = new ForecastRequest(ToWire(horizon), rows);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url) { Content = JsonContent.Create(body, options: Json) };
                request.Headers.Add("x-api-key", settings.ApiKey);
                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var answer = await response.Content.ReadFromJsonAsync<WireResponse>(Json, ct)
                                 ?? throw new UpstreamException("The forecast API answered with an empty body");
                    if (answer.Predictions is null || answer.Predictions.Count != rows.Count)
                    {
                        throw new UpstreamException($"The forecast API answered {answer.Predictions?.Count ?? 0} prediction(s) for {rows.Count} row(s)");
                    }

                    var model = answer.Model ?? new WireModel(null, null, null, null);
                    return new ForecastResponse(answer.Predictions, new ModelInfo(model.Family ?? "?", model.Horizon ?? ToWire(horizon), model.ValidationMae, model.TestMae));
                }

                var text = await response.Content.ReadAsStringAsync(ct);
                var retryable = response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.ServiceUnavailable;
                if (retryable && attempt == 1)
                {
                    logger.LogWarning("Forecast API answered {Status} on the first try (a cold endpoint?); trying once more", (int)response.StatusCode);
                    await Task.Delay(RetryPause, ct);
                    continue;
                }

                throw new UpstreamException($"The forecast API answered {(int)response.StatusCode}: {Truncate(text)}");
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt == 1)
                {
                    logger.LogWarning(ex, "Forecast API request failed on the first try; trying once more");
                    await Task.Delay(RetryPause, ct);
                    continue;
                }

                throw new UpstreamException($"The forecast API did not answer: {ex.Message}", ex);
            }
        }
    }

    public static string ToWire(Horizon horizon) => horizon == Horizon.Day ? "day" : "rest_of_month";

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];

    private sealed record ForecastRequest(string Horizon, IReadOnlyList<IReadOnlyDictionary<string, object>> Rows);

    private sealed record WireResponse(IReadOnlyList<double>? Predictions, WireModel? Model);

    private sealed record WireModel(
        string? Family,
        string? Horizon,
        [property: JsonPropertyName("validation_mae")] double? ValidationMae,
        [property: JsonPropertyName("test_mae")] double? TestMae);
}
