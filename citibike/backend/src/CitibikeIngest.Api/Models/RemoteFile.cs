using System.Globalization;
using System.Text.RegularExpressions;

namespace CitibikeIngest.Api.Models;

/// <summary>One zip in the bucket. Month is null for a yearly bundle (2013-2023 are published per year).</summary>
public sealed record RemoteFile(
    string Name,
    string Region,
    int Year,
    int? Month,
    long SizeBytes,
    DateTimeOffset LastModified)
{
    public bool IsYearlyBundle => Month is null;

    public string Label => Month is { } m ? $"{Year}-{m:00}" : $"{Year} (yearly bundle)";
}

/// <summary>
/// Parses bucket keys: 2021-citibike-tripdata.zip, 202401-citibike-tripdata.zip,
/// JC-202001-citibike-tripdata.csv.zip and the irregular "JC-201708 citibike-tripdata.csv.zip",
/// "JC-202207-citbike-tripdata.csv.zip". Same pattern as FILE_RE in citibike_data.py.
/// </summary>
public static partial class RemoteFileName
{
    [GeneratedRegex(@"^(?<jc>JC-)?(?<year>\d{4})(?<month>\d{2})?[ -]cit(?:i)?bike-tripdata(?:\.csv)?\.zip$")]
    private static partial Regex Pattern();

    public static bool TryParse(string name, out string region, out int year, out int? month)
    {
        region = "";
        year = 0;
        month = null;
        var match = Pattern().Match(name);
        if (!match.Success)
        {
            return false;
        }

        region = match.Groups["jc"].Success ? "jc" : "nyc";
        year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        month = match.Groups["month"].Success
            ? int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture)
            : null;
        return true;
    }
}
