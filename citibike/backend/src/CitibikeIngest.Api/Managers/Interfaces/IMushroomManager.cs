using CitibikeIngest.Api.Dtos;

namespace CitibikeIngest.Api.Managers.Interfaces;

public interface IMushroomManager
{
    Task<MushroomFeatureCatalogDto> GetFeaturesAsync(CancellationToken ct);
    Task<MushroomPredictionDto> PredictAsync(MushroomPredictionRequest request, CancellationToken ct);
}
