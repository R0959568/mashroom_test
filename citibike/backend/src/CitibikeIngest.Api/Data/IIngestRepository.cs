using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Data;

/// <summary>Reads and writes the ingest metadata tables (citibike_meta.ingest_run / ingest_file).</summary>
public interface IIngestRepository
{
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Marks runs left in "running" by a crashed or restarted backend as failed. Returns how many.</summary>
    Task<int> MarkStaleRunsFailedAsync(CancellationToken ct);

    Task<DateTimeOffset?> GetLastSuccessfulRunStartAsync(CancellationToken ct);

    Task<IngestRun?> GetRunningRunAsync(CancellationToken ct);

    /// <summary>Inserts the run and fills in Id and StartedAt. Throws <see cref="ConflictException"/> when a run is already running.</summary>
    Task<IngestRun> CreateRunAsync(IngestRun run, CancellationToken ct);

    Task UpdateRunAsync(IngestRun run, CancellationToken ct);

    Task<IngestRun?> GetRunAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<IngestRun>> ListRunsAsync(int limit, CancellationToken ct);

    /// <summary>Records the files a run will process (status pending) and returns them with their ids.</summary>
    Task<IReadOnlyList<IngestFile>> AddFilesAsync(long runId, IEnumerable<RemoteFile> files, CancellationToken ct);

    Task UpdateFileAsync(IngestFile file, CancellationToken ct);

    Task<IReadOnlyList<IngestFile>> GetRunFilesAsync(long runId, CancellationToken ct);
}
