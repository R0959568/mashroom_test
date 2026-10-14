using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Mappers;
using Microsoft.AspNetCore.Mvc;

namespace CitibikeIngest.Api.Controllers;

[ApiController]
[Route("api/s3files")]
public sealed class S3FilesController(IS3FilesManager manager) : ControllerBase
{
    /// <summary>
    /// Bucket files modified after <paramref name="since"/> (e.g. 2026-09-01 or 2026-09-01T00:00:00Z).
    /// Without the parameter: after the last successful run — exactly what the cron trigger would process.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<NewFilesDto>> List([FromQuery] DateTimeOffset? since, CancellationToken ct)
    {
        var listing = await manager.ListNewFilesAsync(since, ct);
        return Ok(listing.ToDto());
    }
}
