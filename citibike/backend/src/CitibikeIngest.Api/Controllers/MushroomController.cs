using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Managers.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CitibikeIngest.Api.Controllers;

[ApiController]
[Route("api/mushroom")]
public sealed class MushroomController(IMushroomManager manager) : ControllerBase
{
    [HttpGet("features")]
    public async Task<ActionResult<MushroomFeatureCatalogDto>> GetFeatures(CancellationToken ct) =>
        Ok(await manager.GetFeaturesAsync(ct));

    [HttpPost("predict")]
    public async Task<ActionResult<MushroomPredictionDto>> Predict(
        [FromBody] MushroomPredictionRequest request,
        CancellationToken ct) =>
        Ok(await manager.PredictAsync(request, ct));
}
