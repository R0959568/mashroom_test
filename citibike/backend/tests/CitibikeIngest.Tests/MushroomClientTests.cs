using System.Net;
using System.Text;
using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CitibikeIngest.Tests;

public sealed class MushroomClientTests
{
    [Fact]
    public async Task Predict_sends_the_features_and_maps_the_model_response()
    {
        string? requestBody = null;
        var handler = new StubHandler(async (request, ct) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(ct);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri("http://mushroom-model:8000/predict"), request.RequestUri);
            return JsonResponse(
                """{"predictedClass":"poisonous","poisonousProbability":0.7,"threshold":0.5,"modelName":"Random Forest","warning":"educational only"}""");
        });
        using var http = Client(handler);
        var client = new MushroomClient(http, NullLogger<MushroomClient>.Instance);
        var requestDto = new MushroomPredictionRequest(new Dictionary<string, System.Text.Json.JsonElement>());

        var response = await client.PredictAsync(requestDto, CancellationToken.None);

        Assert.Contains("\"features\"", requestBody ?? string.Empty);
        Assert.Equal("poisonous", response.PredictedClass);
        Assert.Equal(0.7, response.PoisonousProbability);
        Assert.Equal(0.5, response.Threshold);
        Assert.Equal("Random Forest", response.ModelName);
    }

    [Fact]
    public async Task Feature_catalog_is_deserialized_from_the_model_service()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            """{"modelName":"Random Forest","threshold":0.5,"features":[{"name":"cap-diameter","kind":"numeric","options":[]}],"warning":"educational only"}""")));
        using var http = Client(handler);
        var client = new MushroomClient(http, NullLogger<MushroomClient>.Instance);

        var catalog = await client.GetFeaturesAsync(CancellationToken.None);

        Assert.Equal("Random Forest", catalog.ModelName);
        Assert.Equal("cap-diameter", Assert.Single(catalog.Features).Name);
    }

    [Fact]
    public async Task Non_success_responses_become_upstream_errors()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("model starting"),
            }));
        using var http = Client(handler);
        var client = new MushroomClient(http, NullLogger<MushroomClient>.Instance);

        var error = await Assert.ThrowsAsync<UpstreamException>(() =>
            client.GetFeaturesAsync(CancellationToken.None));

        Assert.Contains("503", error.Message);
        Assert.Contains("model starting", error.Message);
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://mushroom-model:8000/") };

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
