using System.Globalization;
using System.Xml.Linq;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Data;

/// <summary>
/// Talks to the bucket over plain HTTPS (ListObjectsV2 XML + GET per key). No AWS SDK and no
/// credentials: the bucket is public. Mirrors list_remote_files() / download() in citibike_data.py.
/// </summary>
public sealed class S3BucketClient(HttpClient http, IOptions<IngestOptions> options, ILogger<S3BucketClient> logger)
    : IS3BucketClient
{
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private const int DownloadAttempts = 3;

    private string BucketUrl => options.Value.BucketUrl.EndsWith('/') ? options.Value.BucketUrl : options.Value.BucketUrl + "/";

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken ct)
    {
        var files = new List<RemoteFile>();
        string? continuationToken = null;
        do
        {
            var url = $"{BucketUrl}?list-type=2&max-keys=1000";
            if (continuationToken is not null)
            {
                url += "&continuation-token=" + Uri.EscapeDataString(continuationToken);
            }

            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
            var root = document.Root ?? throw new InvalidOperationException("Empty bucket listing");

            foreach (var item in root.Elements(S3 + "Contents"))
            {
                var key = item.Element(S3 + "Key")?.Value ?? "";
                if (!RemoteFileName.TryParse(key, out var region, out var year, out var month))
                {
                    if (key != "index.html")
                    {
                        logger.LogWarning("Unrecognised file in bucket, ignored: {Key}", key);
                    }

                    continue;
                }

                var size = long.Parse(item.Element(S3 + "Size")?.Value ?? "0", CultureInfo.InvariantCulture);
                var lastModified = DateTimeOffset.Parse(
                    item.Element(S3 + "LastModified")?.Value ?? throw new InvalidOperationException($"No LastModified for {key}"),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                files.Add(new RemoteFile(key, region, year, month, size, lastModified));
            }

            continuationToken = root.Element(S3 + "IsTruncated")?.Value == "true"
                ? root.Element(S3 + "NextContinuationToken")?.Value
                : null;
        }
        while (continuationToken is not null);

        return files.OrderBy(f => f.Region).ThenBy(f => f.Year).ThenBy(f => f.Month ?? 0).ToList();
    }

    public async Task DownloadAsync(RemoteFile file, string destinationPath, CancellationToken ct)
    {
        var partPath = destinationPath + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(file, partPath, ct);
                File.Move(partPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < DownloadAttempts)
            {
                var wait = TimeSpan.FromSeconds(5 * attempt);
                logger.LogWarning(ex, "Download of {File} failed (attempt {Attempt}); retrying in {Wait}s", file.Name, attempt, wait.TotalSeconds);
                await Task.Delay(wait, ct);
            }
        }
    }

    private async Task DownloadOnceAsync(RemoteFile file, string partPath, CancellationToken ct)
    {
        var url = BucketUrl + Uri.EscapeDataString(file.Name);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            await source.CopyToAsync(target, 8 << 20, ct);
        }

        var size = new FileInfo(partPath).Length;
        if (size != file.SizeBytes)
        {
            File.Delete(partPath);
            throw new IOException($"{file.Name}: downloaded {size} bytes, expected {file.SizeBytes}");
        }
    }
}
