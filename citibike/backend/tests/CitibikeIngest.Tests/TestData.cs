using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Sept2026 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // Real names and LastModified values from the bucket listing (September 2026), sizes rounded.
    public static readonly RemoteFile Bundle2021 = new("2021-citibike-tripdata.zip", "nyc", 2021, null, 1_074_415_437, Parse("2024-08-13T12:47:01Z"));
    public static readonly RemoteFile Bundle2020 = new("2020-citibike-tripdata.zip", "nyc", 2020, null, 751_199_232, Parse("2024-07-31T18:18:37Z"));
    public static readonly RemoteFile July2026 = new("202607-citibike-tripdata.zip", "nyc", 2026, 7, 974_302_094, Parse("2026-08-12T16:03:43Z"));
    public static readonly RemoteFile Aug2026 = new("202608-citibike-tripdata.zip", "nyc", 2026, 8, 1_024_034_405, Parse("2026-09-10T15:28:32Z"));
    public static readonly RemoteFile JcAug2026 = new("JC-202608-citibike-tripdata.csv.zip", "jc", 2026, 8, 3_994_745, Parse("2026-09-09T18:19:14Z"));

    public static IOptions<IngestOptions> Options(string? dataDir = null, string regions = "nyc") =>
        Microsoft.Extensions.Options.Options.Create(new IngestOptions
        {
            DataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "citibike-tests", Guid.NewGuid().ToString("N")),
            Regions = regions,
            MinYear = 2021,
            DefaultSince = "2021-01-01",
        });

    public static DateTimeOffset Parse(string iso) =>
        DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);
}
