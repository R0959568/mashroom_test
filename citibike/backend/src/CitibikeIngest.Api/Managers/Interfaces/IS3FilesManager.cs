using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Managers.Interfaces;

public interface IS3FilesManager
{
    /// <summary>Explicit value wins; otherwise the start of the last successful run; otherwise Ingest:DefaultSince.</summary>
    Task<SinceResolution> ResolveSinceAsync(DateTimeOffset? since, CancellationToken ct);

    /// <summary>Bucket files modified after the (resolved) watermark, limited to the configured regions and MinYear.</summary>
    Task<NewFiles> ListNewFilesAsync(DateTimeOffset? since, CancellationToken ct);
}
