using CitibikeIngest.Api.Managers;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using CitibikeIngest.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Tests;

public sealed class IngestManagerTests : IDisposable
{
    private readonly FakeS3BucketClient bucket = new();
    private readonly FakeIngestRepository repository = new();
    private readonly FakeZipConverter converter = new();
    private readonly FakeIngestQueue queue = new();
    private readonly IOptions<IngestOptions> options = TestData.Options();

    public IngestManagerTests()
    {
        bucket.Files.AddRange([TestData.Bundle2020, TestData.Bundle2021, TestData.July2026, TestData.Aug2026, TestData.JcAug2026]);
    }

    public void Dispose()
    {
        if (Directory.Exists(options.Value.DataDir))
        {
            Directory.Delete(options.Value.DataDir, recursive: true);
        }
    }

    private IngestManager Manager() => new(
        new S3FilesManager(bucket, repository, options), bucket, repository, converter, queue, options,
        NullLogger<IngestManager>.Instance);

    // ---- StartRunAsync -----------------------------------------------------------------------

    [Fact]
    public async Task Start_records_the_run_and_queues_it()
    {
        repository.LastSuccessfulRunStart = TestData.Parse("2026-08-01T00:00:00Z");

        var run = await Manager().StartRunAsync(null, "cron", CancellationToken.None);

        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(SinceSource.LastRun, run.SinceSource);
        Assert.Equal(repository.LastSuccessfulRunStart, run.Since);
        Assert.Equal("cron", run.Trigger);
        Assert.Equal([run.Id], queue.Enqueued);
    }

    [Fact]
    public async Task Start_with_explicit_since_uses_it()
    {
        var run = await Manager().StartRunAsync(TestData.Sept2026, null, CancellationToken.None);

        Assert.Equal(SinceSource.Parameter, run.SinceSource);
        Assert.Equal(TestData.Sept2026, run.Since);
        Assert.Equal("manual", run.Trigger);
    }

    [Fact]
    public async Task Start_while_a_run_is_in_progress_conflicts()
    {
        var manager = Manager();
        await manager.StartRunAsync(null, null, CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => manager.StartRunAsync(null, null, CancellationToken.None));
        Assert.Single(queue.Enqueued);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("MANUAL ")]
    public async Task Start_validates_the_trigger(string trigger)
    {
        var task = Manager().StartRunAsync(null, trigger, CancellationToken.None);
        if (trigger.Trim().Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal("manual", (await task).Trigger); // trimmed and lower-cased
        }
        else
        {
            await Assert.ThrowsAsync<ValidationException>(() => task);
        }
    }

    [Fact]
    public async Task Start_rejects_a_future_since()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Manager().StartRunAsync(DateTimeOffset.UtcNow.AddDays(1), null, CancellationToken.None));
    }

    [Fact]
    public async Task Get_unknown_run_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Manager().GetRunAsync(42, CancellationToken.None));
    }

    // ---- ExecuteRunAsync ---------------------------------------------------------------------

    [Fact]
    public async Task Execute_downloads_converts_and_deletes_each_new_file()
    {
        var manager = Manager();
        var run = await manager.StartRunAsync(TestData.Sept2026, null, CancellationToken.None);

        await manager.ExecuteRunAsync(run.Id, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Null(run.Error);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal((1, 1, 0), (run.FilesFound, run.FilesDone, run.FilesFailed));
        Assert.Equal(["202608-citibike-tripdata.zip"], bucket.Downloaded);

        var call = Assert.Single(converter.Calls);
        Assert.Equal(Path.Combine(options.Value.LandingDir, "202608-citibike-tripdata.zip"), call.ZipPath);
        Assert.Equal("nyc", call.Region);
        Assert.True(call.ZipExistedAtCall);
        Assert.False(File.Exists(call.ZipPath)); // zip removed after conversion

        var file = Assert.Single(repository.Files);
        Assert.Equal(FileStatus.Done, file.Status);
        Assert.Equal((1, 1000L, 4096L), (file.MonthsWritten, file.RowsWritten, file.ParquetBytes));
        Assert.NotNull(file.DurationSeconds);
        Assert.NotNull(file.ProcessedAt);
    }

    [Fact]
    public async Task Execute_continues_after_a_failed_file_and_marks_the_run_failed()
    {
        converter.FailFor.Add("2021-citibike-tripdata.zip");
        bucket.FailDownloads.Add("202607-citibike-tripdata.zip");
        var manager = Manager();
        var run = await manager.StartRunAsync(null, null, CancellationToken.None); // backfill: 2021 bundle + 2 monthly files

        await manager.ExecuteRunAsync(run.Id, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal((3, 1, 2), (run.FilesFound, run.FilesDone, run.FilesFailed));
        Assert.Contains("2 file(s) failed", run.Error);

        var byName = repository.Files.ToDictionary(f => f.FileName);
        Assert.Equal(FileStatus.Failed, byName["2021-citibike-tripdata.zip"].Status);
        Assert.Contains("boom", byName["2021-citibike-tripdata.zip"].Error);
        Assert.Equal(FileStatus.Failed, byName["202607-citibike-tripdata.zip"].Status);
        Assert.Contains("503", byName["202607-citibike-tripdata.zip"].Error);
        Assert.Equal(FileStatus.Done, byName["202608-citibike-tripdata.zip"].Status);
        Assert.Empty(Directory.GetFiles(options.Value.LandingDir)); // no zips left behind, not even failed ones
    }

    [Fact]
    public async Task Execute_with_nothing_new_succeeds_with_zero_files()
    {
        var manager = Manager();
        var run = await manager.StartRunAsync(TestData.Aug2026.LastModified, null, CancellationToken.None);

        await manager.ExecuteRunAsync(run.Id, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(0, run.FilesFound);
        Assert.Empty(bucket.Downloaded);
    }

    [Fact]
    public async Task Execute_ignores_runs_that_are_not_running()
    {
        var manager = Manager();
        var run = await manager.StartRunAsync(null, null, CancellationToken.None);
        run.Status = RunStatus.Failed; // e.g. marked stale by StartupInitializer

        await manager.ExecuteRunAsync(run.Id, CancellationToken.None);

        Assert.Empty(bucket.Downloaded);
        Assert.Empty(repository.Files);
    }
}
