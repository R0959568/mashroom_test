using CitibikeIngest.Api.Managers;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Tests.Fakes;

namespace CitibikeIngest.Tests;

public class S3FilesManagerTests
{
    private readonly FakeS3BucketClient bucket = new();
    private readonly FakeIngestRepository repository = new();

    public S3FilesManagerTests()
    {
        bucket.Files.AddRange([TestData.Bundle2020, TestData.Bundle2021, TestData.July2026, TestData.Aug2026, TestData.JcAug2026]);
    }

    private S3FilesManager Manager(string regions = "nyc") => new(bucket, repository, TestData.Options(regions: regions));

    [Fact]
    public async Task Explicit_since_wins_over_last_run()
    {
        repository.LastSuccessfulRunStart = TestData.Parse("2026-08-01T00:00:00Z");

        var resolved = await Manager().ResolveSinceAsync(TestData.Sept2026, CancellationToken.None);

        Assert.Equal(SinceSource.Parameter, resolved.Source);
        Assert.Equal(TestData.Sept2026, resolved.Since);
    }

    [Fact]
    public async Task Without_parameter_the_last_successful_run_start_is_the_watermark()
    {
        repository.LastSuccessfulRunStart = TestData.Parse("2026-08-01T00:00:00Z");

        var resolved = await Manager().ResolveSinceAsync(null, CancellationToken.None);

        Assert.Equal(SinceSource.LastRun, resolved.Source);
        Assert.Equal(repository.LastSuccessfulRunStart, resolved.Since);
    }

    [Fact]
    public async Task Without_any_run_the_configured_default_is_used()
    {
        var resolved = await Manager().ResolveSinceAsync(null, CancellationToken.None);

        Assert.Equal(SinceSource.Default, resolved.Source);
        Assert.Equal(TestData.Parse("2021-01-01T00:00:00Z"), resolved.Since);
    }

    [Fact]
    public async Task September_2026_yields_only_the_august_file()
    {
        var listing = await Manager().ListNewFilesAsync(TestData.Sept2026, CancellationToken.None);

        Assert.Equal(["202608-citibike-tripdata.zip"], listing.Files.Select(f => f.Name));
        Assert.Equal(TestData.Aug2026.SizeBytes, listing.TotalBytes);
    }

    [Fact]
    public async Task Modified_exactly_at_the_watermark_is_not_new()
    {
        var listing = await Manager().ListNewFilesAsync(TestData.Aug2026.LastModified, CancellationToken.None);

        Assert.Empty(listing.Files);
    }

    [Fact]
    public async Task Backfill_skips_years_before_min_year_even_when_uploaded_recently()
    {
        var listing = await Manager().ListNewFilesAsync(null, CancellationToken.None);

        Assert.Equal(["2021-citibike-tripdata.zip", "202607-citibike-tripdata.zip", "202608-citibike-tripdata.zip"],
            listing.Files.Select(f => f.Name));
        Assert.DoesNotContain(listing.Files, f => f.Year < 2021);
    }

    [Fact]
    public async Task Regions_setting_adds_jersey_city()
    {
        var listing = await Manager(regions: "nyc, jc").ListNewFilesAsync(TestData.Sept2026, CancellationToken.None);

        Assert.Equal(["JC-202608-citibike-tripdata.csv.zip", "202608-citibike-tripdata.zip"], listing.Files.Select(f => f.Name));
    }
}
