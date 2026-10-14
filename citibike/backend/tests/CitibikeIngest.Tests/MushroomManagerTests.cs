using System.Text.Json;
using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Managers;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests;

public sealed class MushroomManagerTests
{
    [Fact]
    public async Task Valid_features_are_forwarded_to_the_inference_client()
    {
        var client = new FakeMushroomClient();
        var manager = new MushroomManager(client);
        var request = Request(
            """{"cap-diameter":5,"stem-height":6,"stem-width":10,"spore-print-color":"g","gill-color":"w","habitat":"g","season":"a","ring-type":"p","cap-shape":"x","stem-surface":"y"}""");

        var result = await manager.PredictAsync(request, CancellationToken.None);

        Assert.Equal("poisonous", result.PredictedClass);
        Assert.Same(request, client.Request);
    }

    [Fact]
    public async Task Missing_and_unknown_features_are_rejected_before_inference()
    {
        var client = new FakeMushroomClient();
        var manager = new MushroomManager(client);

        var missing = await Assert.ThrowsAsync<ValidationException>(() =>
            manager.PredictAsync(Request("""{"cap-diameter":5}"""), CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<ValidationException>(() =>
            manager.PredictAsync(Request(
                """{"cap-diameter":5,"stem-height":6,"stem-width":10,"spore-print-color":"g","gill-color":"w","habitat":"g","season":"a","ring-type":"p","cap-shape":"x","stem-surface":"y","unexpected":1}"""),
                CancellationToken.None));

        Assert.Contains("missing", missing.Message);
        Assert.Contains("unknown", unknown.Message);
        Assert.Null(client.Request);
    }

    [Fact]
    public async Task Invalid_numeric_and_categorical_values_are_rejected()
    {
        var client = new FakeMushroomClient();
        var manager = new MushroomManager(client);
        var negativeMeasurement = Request(
            """{"cap-diameter":-1,"stem-height":6,"stem-width":10,"spore-print-color":"g","gill-color":"w","habitat":"g","season":"a","ring-type":"p","cap-shape":"x","stem-surface":"y"}""");
        var wrongCategoryType = Request(
            """{"cap-diameter":5,"stem-height":6,"stem-width":10,"spore-print-color":"g","gill-color":"w","habitat":3,"season":"a","ring-type":"p","cap-shape":"x","stem-surface":"y"}""");

        await Assert.ThrowsAsync<ValidationException>(() =>
            manager.PredictAsync(negativeMeasurement, CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            manager.PredictAsync(wrongCategoryType, CancellationToken.None));
        Assert.Null(client.Request);
    }

    private static MushroomPredictionRequest Request(string json)
    {
        using var document = JsonDocument.Parse(json);
        var features = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        return new MushroomPredictionRequest(features);
    }

    private sealed class FakeMushroomClient : IMushroomClient
    {
        public MushroomPredictionRequest? Request { get; private set; }

        public Task<MushroomFeatureCatalogDto> GetFeaturesAsync(CancellationToken ct) =>
            Task.FromResult(new MushroomFeatureCatalogDto("Random Forest", 0.5, [], "educational only"));

        public Task<MushroomPredictionDto> PredictAsync(MushroomPredictionRequest request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new MushroomPredictionDto("poisonous", 0.5689, 0.5, "Random Forest", "educational only"));
        }
    }
}
