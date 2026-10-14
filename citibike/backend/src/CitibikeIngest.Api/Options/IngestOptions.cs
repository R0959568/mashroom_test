using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace CitibikeIngest.Api.Options;

/// <summary>
/// Settings for the bucket listing, the data volume and the Python converter.
/// Bound from the "Ingest" section; in the container every value is an Ingest__Xxx environment variable.
/// </summary>
public sealed class IngestOptions
{
    public const string Section = "Ingest";

    /// <summary>Public bucket Citi Bike publishes to. Anonymous read, no credentials involved.</summary>
    [Required]
    public string BucketUrl { get; set; } = "https://s3.amazonaws.com/tripdata/";

    /// <summary>Root of the data volume: zips land in landing/, Parquet goes to raw/&lt;SchemaVersion&gt;/.</summary>
    [Required]
    public string DataDir { get; set; } = "/data/citibike";

    public string SchemaVersion { get; set; } = "v2";

    /// <summary>
    /// Files for earlier years are ignored even when they are re-uploaded after the watermark:
    /// the pre-2021 column layout is out of scope (TODO.md) and must never land in the v2 folder.
    /// </summary>
    public int MinYear { get; set; } = 2021;

    /// <summary>Comma-separated regions to ingest: nyc and/or jc (Jersey City).</summary>
    public string Regions { get; set; } = "nyc";

    /// <summary>Watermark for the very first run (ISO date). Later runs use the start time of the last successful run.</summary>
    public string DefaultSince { get; set; } = "2021-01-01";

    public string PythonExecutable { get; set; } = "python3";

    public string ConverterScript { get; set; } = "/app/ingest/convert_zip.py";

    /// <summary>Required in the X-Admin-Token header on POST /api/ingest/runs. Unset = no check (local dev only).</summary>
    public string? AdminToken { get; set; }

    public string LandingDir => Path.Combine(DataDir, "landing");

    public string RawDir => Path.Combine(DataDir, "raw", SchemaVersion);

    public IReadOnlySet<string> RegionSet => Regions
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(r => r.ToLowerInvariant())
        .ToHashSet();

    public DateTimeOffset DefaultSinceUtc =>
        DateTimeOffset.Parse(DefaultSince, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
