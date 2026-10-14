using System.Net.Http.Json;
using System.Text.Json;
using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Data;

public sealed class MushroomClient(HttpClient http, ILogger<MushroomClient> logger) : IMushroomClient
{
    public Task<MushroomFeatureCatalogDto> GetFeaturesAsync(CancellationToken ct) =>
        GetAsync<MushroomFeatureCatalogDto>("features", ct);

    public Task<MushroomPredictionDto> PredictAsync(MushroomPredictionRequest request, CancellationToken ct) =>
        PostAsync<MushroomPredictionRequest, MushroomPredictionDto>("predict", request, ct);

    private async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(path, ct);
            return await ReadResponseAsync<TResponse>(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is JsonException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogError(ex, "Mushroom inference service request failed");
            throw new UpstreamException("The mushroom inference service did not answer with a valid response", ex);
        }
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, body, ct);
            return await ReadResponseAsync<TResponse>(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is JsonException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogError(ex, "Mushroom inference service request failed");
            throw new UpstreamException("The mushroom inference service did not answer with a valid response", ex);
        }
    }

    private static async Task<TResponse> ReadResponseAsync<TResponse>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new UpstreamException($"The mushroom inference service answered {(int)response.StatusCode}: {Truncate(detail)}");
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct)
               ?? throw new UpstreamException("The mushroom inference service answered with an empty body");
    }

    private static string Truncate(string detail) => detail.Length <= 300 ? detail : detail[..300];
}
