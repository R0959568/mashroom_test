using System.Diagnostics;
using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Infrastructure;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Managers;

public sealed class IngestManager(
    IS3FilesManager s3Files,
    IS3BucketClient bucket,
    IIngestRepository repository,
    IZipConverter converter,
    IIngestQueue queue,
    IOptions<IngestOptions> options,
    ILogger<IngestManager> logger) : IIngestManager
{
    private const int MaxErrorLength = 2000;

    public async Task<IngestRun> StartRunAsync(DateTimeOffset? since, string? trigger, CancellationToken ct)
    {
        trigger = RunTrigger.Normalize(trigger);

        if (since is { } s && s > DateTimeOffset.UtcNow)
        {
            throw new ValidationException("since cannot be in the future");
        }

        if (await repository.GetRunningRunAsync(ct) is { } running)
        {
            throw new ConflictException($"Run {running.Id} is still in progress (started {running.StartedAt:u})");
        }

        var resolved = await s3Files.ResolveSinceAsync(since, ct);
        var run = await repository.CreateRunAsync(new IngestRun
        {
            Since = resolved.Since,
            SinceSource = resolved.Source,
            Trigger = trigger,
        }, ct);

        queue.Enqueue(run.Id);
        logger.LogInformation("Run {RunId} queued ({Trigger}); files modified after {Since:u} ({Source})",
            run.Id, trigger, run.Since, run.SinceSource);
        return run;
    }

    public async Task<IngestRun> GetRunAsync(long id, CancellationToken ct) =>
        await repository.GetRunAsync(id, ct) ?? throw new NotFoundException($"Run {id} does not exist");

    public async Task<IReadOnlyList<IngestFile>> GetRunFilesAsync(long id, CancellationToken ct)
    {
        _ = await GetRunAsync(id, ct);
        return await repository.GetRunFilesAsync(id, ct);
    }

    public async Task<IReadOnlyList<IngestRun>> ListRunsAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 200)
        {
            throw new ValidationException("limit must be between 1 and 200");
        }

        return await repository.ListRunsAsync(limit, ct);
    }

    public async Task ExecuteRunAsync(long runId, CancellationToken ct)
    {
        var run = await repository.GetRunAsync(runId, ct);
        if (run is null || run.Status != RunStatus.Running)
        {
            logger.LogWarning("Run {RunId} was not found or is no longer running; skipped", runId);
            return;
        }

        try
        {
            // The run's watermark was fixed when it was created, so it is passed as an explicit value here.
            var listing = await s3Files.ListNewFilesAsync(run.Since, ct);
            var byName = listing.Files.ToDictionary(f => f.Name);
            var files = await repository.AddFilesAsync(runId, listing.Files, ct);
            run.FilesFound = files.Count;
            await repository.UpdateRunAsync(run, ct);
            logger.LogInformation("Run {RunId}: {Count} file(s) modified after {Since:u}, {Bytes:N0} bytes to download",
                runId, files.Count, run.Since, listing.TotalBytes);

            foreach (var file in files)
            {
                var ok = await ProcessFileAsync(file, byName[file.FileName], ct);
                if (ok)
                {
                    run.FilesDone++;
                }
                else
                {
                    run.FilesFailed++;
                }

                await repository.UpdateRunAsync(run, ct);
            }

            run.Status = run.FilesFailed == 0 ? RunStatus.Succeeded : RunStatus.Failed;
            run.Error = run.FilesFailed == 0 ? null : $"{run.FilesFailed} file(s) failed; see the file list";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = RunStatus.Failed;
            run.Error = "Cancelled: the backend shut down during the run";
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} failed", runId);
            run.Status = RunStatus.Failed;
            run.Error = Truncate(ex.Message);
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            // A failed run keeps the watermark where it was, so the next run retries the same files.
            await repository.UpdateRunAsync(run, CancellationToken.None);
            logger.LogInformation("Run {RunId} {Status}: {Done} done, {Failed} failed", runId, run.Status, run.FilesDone, run.FilesFailed);
        }
    }

    private async Task<bool> ProcessFileAsync(IngestFile file, RemoteFile remote, CancellationToken ct)
    {
        var settings = options.Value;
        Directory.CreateDirectory(settings.LandingDir);
        var zipPath = Path.Combine(settings.LandingDir, file.FileName);
        var stopwatch = Stopwatch.StartNew();
        var ok = false;
        try
        {
            file.Status = FileStatus.Downloading;
            await repository.UpdateFileAsync(file, ct);
            logger.LogInformation("{File}: downloading {Bytes:N0} bytes", file.FileName, file.SizeBytes);
            await bucket.DownloadAsync(remote, zipPath, ct);

            file.Status = FileStatus.Converting;
            await repository.UpdateFileAsync(file, ct);
            var result = await converter.ConvertAsync(zipPath, file.Region, ct);

            file.MonthsWritten = result.Months.Count;
            file.RowsWritten = result.TotalRows;
            file.ParquetBytes = result.TotalParquetBytes;
            file.Status = FileStatus.Done;
            ok = true;
            logger.LogInformation("{File}: {Months} month(s), {Rows:N0} rows, {Bytes:N0} Parquet bytes in {Seconds:F0}s",
                file.FileName, file.MonthsWritten, file.RowsWritten, file.ParquetBytes, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{File}: failed", file.FileName);
            file.Status = FileStatus.Failed;
            file.Error = Truncate(ex.Message);
        }
        finally
        {
            file.DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
            file.ProcessedAt = DateTimeOffset.UtcNow;
            DeleteQuietly(zipPath);          // disk is tight: the zip never outlives its conversion
            DeleteQuietly(zipPath + ".part");
            await repository.UpdateFileAsync(file, CancellationToken.None);
        }

        return ok;
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    private static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
}
