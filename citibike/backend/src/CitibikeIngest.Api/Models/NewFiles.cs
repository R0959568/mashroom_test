namespace CitibikeIngest.Api.Models;

/// <summary>Where the "since" watermark of a listing or run came from.</summary>
public enum SinceSource
{
    /// <summary>Passed explicitly by the caller (query parameter or request body).</summary>
    Parameter,

    /// <summary>Start time of the last successful run in citibike_meta.ingest_run.</summary>
    LastRun,

    /// <summary>Ingest:DefaultSince — no successful run yet (first backfill).</summary>
    Default,
}

public sealed record SinceResolution(DateTimeOffset Since, SinceSource Source);

/// <summary>Bucket files modified after the watermark, restricted to the configured regions and years.</summary>
public sealed record NewFiles(SinceResolution Since, IReadOnlyList<RemoteFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.SizeBytes);
}
