using System.Text.Json;
using System.Text.Json.Serialization;

namespace CitibikeIngest.Api.Dtos;

public sealed record MushroomPredictionRequest(
    [property: JsonPropertyName("features")] IReadOnlyDictionary<string, JsonElement> Features);

public sealed record MushroomFeatureDto(
    string Name,
    string Kind,
    IReadOnlyList<string> Options);

public sealed record MushroomFeatureCatalogDto(
    string ModelName,
    double Threshold,
    IReadOnlyList<MushroomFeatureDto> Features,
    string Warning);

public sealed record MushroomPredictionDto(
    string PredictedClass,
    double PoisonousProbability,
    double Threshold,
    string ModelName,
    string Warning);
