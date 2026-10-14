namespace CitibikeIngest.Api.Models;

public enum RunStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>One ingest run: row in citibike_meta.ingest_run. The start time of the last succeeded run is the next run's watermark.</summary>
public sealed class IngestRun
{
    public long Id { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>Files with LastModified strictly after this instant are processed.</summary>
    public DateTimeOffset Since { get; set; }

    public SinceSource SinceSource { get; set; }

    /// <summary>"manual" (UI / hand-made call) or "cron".</summary>
    public string Trigger { get; set; } = "manual";

    public int FilesFound { get; set; }

    public int FilesDone { get; set; }

    public int FilesFailed { get; set; }

    public string? Error { get; set; }
}
