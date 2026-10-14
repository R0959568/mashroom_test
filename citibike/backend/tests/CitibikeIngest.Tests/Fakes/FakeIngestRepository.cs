using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests.Fakes;

/// <summary>In-memory stand-in for the Postgres repository. Same rules as the real one: one running run at a time.</summary>
public sealed class FakeIngestRepository : IIngestRepository
{
    public List<IngestRun> Runs { get; } = [];

    public List<IngestFile> Files { get; } = [];

    public DateTimeOffset? LastSuccessfulRunStart { get; set; }

    public int SchemaCalls { get; private set; }

    public Task EnsureSchemaAsync(CancellationToken ct)
    {
        SchemaCalls++;
        return Task.CompletedTask;
    }

    public Task<int> MarkStaleRunsFailedAsync(CancellationToken ct)
    {
        var stale = Runs.Where(r => r.Status == RunStatus.Running).ToList();
        stale.ForEach(r => r.Status = RunStatus.Failed);
        return Task.FromResult(stale.Count);
    }

    public Task<DateTimeOffset?> GetLastSuccessfulRunStartAsync(CancellationToken ct) =>
        Task.FromResult(LastSuccessfulRunStart);

    public Task<IngestRun?> GetRunningRunAsync(CancellationToken ct) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.Status == RunStatus.Running));

    public Task<IngestRun> CreateRunAsync(IngestRun run, CancellationToken ct)
    {
        if (Runs.Any(r => r.Status == RunStatus.Running))
        {
            throw new ConflictException("An ingest run is already in progress");
        }

        run.Id = Runs.Count + 1;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.Status = RunStatus.Running;
        Runs.Add(run);
        return Task.FromResult(run);
    }

    public Task UpdateRunAsync(IngestRun run, CancellationToken ct) => Task.CompletedTask; // same instance

    public Task<IngestRun?> GetRunAsync(long id, CancellationToken ct) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));

    public Task<IReadOnlyList<IngestRun>> ListRunsAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IngestRun>>(Runs.OrderByDescending(r => r.Id).Take(limit).ToList());

    public Task<IReadOnlyList<IngestFile>> AddFilesAsync(long runId, IEnumerable<RemoteFile> files, CancellationToken ct)
    {
        var added = new List<IngestFile>();
        foreach (var f in files)
        {
            var file = new IngestFile
            {
                Id = Files.Count + 1,
                RunId = runId,
                FileName = f.Name,
                Region = f.Region,
                LastModified = f.LastModified,
                SizeBytes = f.SizeBytes,
            };
            Files.Add(file);
            added.Add(file);
        }

        return Task.FromResult<IReadOnlyList<IngestFile>>(added);
    }

    public Task UpdateFileAsync(IngestFile file, CancellationToken ct) => Task.CompletedTask; // same instance

    public Task<IReadOnlyList<IngestFile>> GetRunFilesAsync(long runId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IngestFile>>(Files.Where(f => f.RunId == runId).ToList());
}
