using CitibikeIngest.Api.Infrastructure;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests.Fakes;

public sealed class FakeZipConverter : IZipConverter
{
    public List<(string ZipPath, string Region, bool ZipExistedAtCall)> Calls { get; } = [];

    /// <summary>Zip file names (not paths) whose conversion should throw.</summary>
    public HashSet<string> FailFor { get; } = [];

    public Func<string, ConversionResult> ResultFor { get; set; } = zipPath =>
        new ConversionResult([new ConvertedMonth("nyc", 2026, 8, Rows: 1000, ParquetBytes: 4096, Seconds: 1.5)]);

    public Task<ConversionResult> ConvertAsync(string zipPath, string region, CancellationToken ct)
    {
        Calls.Add((zipPath, region, File.Exists(zipPath)));
        if (FailFor.Contains(Path.GetFileName(zipPath)))
        {
            throw new InvalidOperationException("Converter exited with code 1: boom");
        }

        return Task.FromResult(ResultFor(zipPath));
    }
}
