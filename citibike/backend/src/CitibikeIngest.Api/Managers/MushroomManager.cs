using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Managers;

public sealed class MushroomManager(IMushroomClient client) : IMushroomManager
{
    private static readonly string[] NumericFeatures = ["cap-diameter", "stem-height", "stem-width"];
    private static readonly string[] CategoricalFeatures =
    [
        "spore-print-color", "gill-color", "habitat", "season", "ring-type", "cap-shape", "stem-surface",
    ];

    public Task<MushroomFeatureCatalogDto> GetFeaturesAsync(CancellationToken ct) => client.GetFeaturesAsync(ct);

    public Task<MushroomPredictionDto> PredictAsync(MushroomPredictionRequest request, CancellationToken ct)
    {
        if (request.Features is null)
        {
            throw new ValidationException("The features object is required");
        }

        var supplied = request.Features.Keys.ToHashSet(StringComparer.Ordinal);
        var expected = NumericFeatures.Concat(CategoricalFeatures).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(supplied).ToArray();
        var unknown = supplied.Except(expected).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
        {
            var details = new List<string>();
            if (missing.Length > 0) details.Add($"missing: {string.Join(", ", missing)}");
            if (unknown.Length > 0) details.Add($"unknown: {string.Join(", ", unknown)}");
            throw new ValidationException($"Feature names do not match the model ({string.Join("; ", details)})");
        }

        foreach (var name in NumericFeatures)
        {
            var value = request.Features[name];
            if (value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
            if (value.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0)
            {
                throw new ValidationException($"{name} must be a finite, non-negative number or null");
            }
        }

        foreach (var name in CategoricalFeatures)
        {
            var value = request.Features[name];
            if (value.ValueKind is not System.Text.Json.JsonValueKind.Null and not System.Text.Json.JsonValueKind.String)
            {
                throw new ValidationException($"{name} must be a string or null");
            }
        }

        return client.PredictAsync(request, ct);
    }
}
