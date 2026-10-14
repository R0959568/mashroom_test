using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Mappers;

public static class IngestMapper
{
    public static IngestRunDto ToDto(this IngestRun run) => new(
        run.Id,
        run.Status.ToApi(),
        run.StartedAt,
        run.FinishedAt,
        run.Since,
        run.SinceSource.ToApi(),
        run.Trigger,
        run.FilesFound,
        run.FilesDone,
        run.FilesFailed,
        run.Error);

    public static IngestFileDto ToDto(this IngestFile file) => new(
        file.Id,
        file.FileName,
        file.Region,
        file.Status.ToApi(),
        file.LastModified,
        file.SizeBytes,
        file.MonthsWritten,
        file.RowsWritten,
        file.ParquetBytes,
        file.DurationSeconds,
        file.Error,
        file.ProcessedAt);

    public static IngestRunDetailDto ToDetailDto(this IngestRun run, IReadOnlyList<IngestFile> files) =>
        new(run.ToDto(), files.Select(ToDto).ToList());
}
