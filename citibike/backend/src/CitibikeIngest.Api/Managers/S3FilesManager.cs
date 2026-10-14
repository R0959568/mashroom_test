using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Managers;

public sealed class S3FilesManager(
    IS3BucketClient bucket,
    IIngestRepository repository,
    IOptions<IngestOptions> options) : IS3FilesManager
{
    public async Task<SinceResolution> ResolveSinceAsync(DateTimeOffset? since, CancellationToken ct)
    {
        if (since is { } explicitSince)
        {
            return new SinceResolution(explicitSince, SinceSource.Parameter);
        }

        // The watermark is the *start* of the last successful run, not its end: a file uploaded while
        // that run was busy has LastModified > start and is picked up next time.
        var lastRun = await repository.GetLastSuccessfulRunStartAsync(ct);
        return lastRun is { } start
            ? new SinceResolution(start, SinceSource.LastRun)
            : new SinceResolution(options.Value.DefaultSinceUtc, SinceSource.Default);
    }

    public async Task<NewFiles> ListNewFilesAsync(DateTimeOffset? since, CancellationToken ct)
    {
        var resolved = await ResolveSinceAsync(since, ct);
        var settings = options.Value;
        var regions = settings.RegionSet;

        var all = await bucket.ListAsync(ct);
        var files = all
            .Where(f => f.LastModified > resolved.Since)
            .Where(f => f.Year >= settings.MinYear)
            .Where(f => regions.Contains(f.Region))
            .OrderBy(f => f.LastModified)
            .ThenBy(f => f.Name)
            .ToList();

        return new NewFiles(resolved, files);
    }
}
