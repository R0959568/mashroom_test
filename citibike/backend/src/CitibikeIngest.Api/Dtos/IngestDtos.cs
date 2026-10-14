namespace CitibikeIngest.Api.Dtos;

/// <summary>Body of POST /api/ingest/runs. Both fields optional: since defaults to the last successful run, trigger to "manual".</summary>
public sealed record StartRunRequestDto(DateTimeOffset? Since, string? Trigger);

public sealed record IngestRunDto(
    long Id,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset Since,
    string SinceSource,
    string Trigger,
    int FilesFound,
    int FilesDone,
    int FilesFailed,
    string? Error);

public sealed record IngestFileDto(
    long Id,
    string FileName,
    string Region,
    string Status,
    DateTimeOffset LastModified,
    long SizeBytes,
    int MonthsWritten,
    long RowsWritten,
    long ParquetBytes,
    double? DurationSeconds,
    string? Error,
    DateTimeOffset? ProcessedAt);

public sealed record IngestRunDetailDto(IngestRunDto Run, IReadOnlyList<IngestFileDto> Files);
