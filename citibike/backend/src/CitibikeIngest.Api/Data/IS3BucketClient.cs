using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Data;

/// <summary>Anonymous access to the public tripdata bucket: listing (with LastModified) and streaming downloads.</summary>
public interface IS3BucketClient
{
    /// <summary>Every trip-data zip in the bucket, sorted by region, year, month. Unrecognised keys are ignored.</summary>
    Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken ct);

    /// <summary>Streams the zip to <paramref name="destinationPath"/>; the size is verified against the listing.</summary>
    Task DownloadAsync(RemoteFile file, string destinationPath, CancellationToken ct);
}
