using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Mappers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CitibikeIngest.Api.Controllers;

[ApiController]
[Route("api/ingest/runs")]
public sealed class IngestRunsController(IIngestManager manager) : ControllerBase
{
    /// <summary>Starts a run in the background (202). Poll GET /api/ingest/runs/{id} for progress. Requires X-Admin-Token.</summary>
    [HttpPost]
    public async Task<ActionResult<IngestRunDto>> Start(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StartRunRequestDto? request,
        CancellationToken ct)
    {
        var run = await manager.StartRunAsync(request?.Since, request?.Trigger, ct);
        return AcceptedAtAction(nameof(Get), new { id = run.Id }, run.ToDto());
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IngestRunDto>>> List([FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var runs = await manager.ListRunsAsync(limit, ct);
        return Ok(runs.Select(r => r.ToDto()).ToList());
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<IngestRunDetailDto>> Get(long id, CancellationToken ct)
    {
        var run = await manager.GetRunAsync(id, ct);
        var files = await manager.GetRunFilesAsync(id, ct);
        return Ok(run.ToDetailDto(files));
    }
}
