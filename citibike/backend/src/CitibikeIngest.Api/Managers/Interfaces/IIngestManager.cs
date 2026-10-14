using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Managers.Interfaces;

public interface IIngestManager
{
    /// <summary>Records a run and queues it for the background worker. Throws <see cref="ConflictException"/> if one is running.</summary>
    Task<IngestRun> StartRunAsync(DateTimeOffset? since, string? trigger, CancellationToken ct);

    Task<IngestRun> GetRunAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<IngestFile>> GetRunFilesAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<IngestRun>> ListRunsAsync(int limit, CancellationToken ct);

    /// <summary>Does the work of a queued run: list, download, convert, delete zip, record per file. Called by the worker only.</summary>
    Task ExecuteRunAsync(long runId, CancellationToken ct);
}
