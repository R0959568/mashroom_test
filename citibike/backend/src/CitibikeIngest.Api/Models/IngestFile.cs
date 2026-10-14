namespace CitibikeIngest.Api.Models;

public enum FileStatus
{
    Pending,
    Downloading,
    Converting,
    Done,
    Failed,
}

/// <summary>One bucket file handled by a run: row in citibike_meta.ingest_file.</summary>
public sealed class IngestFile
{
    public long Id { get; set; }

    public long RunId { get; set; }

    public string FileName { get; set; } = "";

    public string Region { get; set; } = "";

    public DateTimeOffset LastModified { get; set; }

    public long SizeBytes { get; set; }

    public FileStatus Status { get; set; } = FileStatus.Pending;

    public int MonthsWritten { get; set; }

    public long RowsWritten { get; set; }

    public long ParquetBytes { get; set; }

    public double? DurationSeconds { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}
