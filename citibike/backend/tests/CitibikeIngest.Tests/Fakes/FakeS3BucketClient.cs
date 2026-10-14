using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests.Fakes;

public sealed class FakeS3BucketClient : IS3BucketClient
{
    public List<RemoteFile> Files { get; } = [];

    public List<string> Downloaded { get; } = [];

    /// <summary>File names whose download should throw.</summary>
    public HashSet<string> FailDownloads { get; } = [];

    public Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemoteFile>>(Files.ToList());

    public Task DownloadAsync(RemoteFile file, string destinationPath, CancellationToken ct)
    {
        if (FailDownloads.Contains(file.Name))
        {
            throw new HttpRequestException($"503 for {file.Name}");
        }

        Downloaded.Add(file.Name);
        File.WriteAllBytes(destinationPath, new byte[16]);
        return Task.CompletedTask;
    }
}
