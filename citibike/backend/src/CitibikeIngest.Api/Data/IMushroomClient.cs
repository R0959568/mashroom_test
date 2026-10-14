using CitibikeIngest.Api.Dtos;

namespace CitibikeIngest.Api.Data;

public interface IMushroomClient
{
    Task<MushroomFeatureCatalogDto> GetFeaturesAsync(CancellationToken ct);
    Task<MushroomPredictionDto> PredictAsync(MushroomPredictionRequest request, CancellationToken ct);
}
