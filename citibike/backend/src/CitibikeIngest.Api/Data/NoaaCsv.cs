using System.Globalization;
using System.Text;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Data;

/// <summary>
/// Parses the CSV of the NCEI daily-summaries service: a header row with quoted column names
/// ("STATION","DATE","AWND",...), one row per day, empty fields for variables not reported that day.
/// Columns are looked up by name, so their order does not matter.
/// </summary>
public static class NoaaCsv
{
    public static IReadOnlyList<WeatherDay> Parse(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return [];   // NOAA answers with an empty body when the range holds no data yet
        }

        var index = SplitLine(lines[0])
            .Select((name, i) => (name, i))
            .ToDictionary(c => c.name, c => c.i, StringComparer.OrdinalIgnoreCase);
        if (!index.ContainsKey("STATION") || !index.ContainsKey("DATE"))
        {
            throw new FormatException($"Unexpected NOAA response, no STATION/DATE header: {Shorten(lines[0])}");
        }

        var days = new List<WeatherDay>(lines.Length - 1);
        foreach (var line in lines.Skip(1))
        {
            var fields = SplitLine(line);
            var date = Field(fields, index, "DATE") ?? throw new FormatException($"Row without date: {Shorten(line)}");
            days.Add(new WeatherDay(
                Station: Field(fields, index, "STATION") ?? throw new FormatException($"Row without station: {Shorten(line)}"),
                Day: DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                TmaxC: Number(fields, index, "TMAX"),
                TminC: Number(fields, index, "TMIN"),
                PrcpMm: Number(fields, index, "PRCP"),
                SnowMm: Number(fields, index, "SNOW"),
                SnowDepthMm: Number(fields, index, "SNWD"),
                WindMs: Number(fields, index, "AWND")));
        }

        return days;
    }

    private static string? Field(List<string> fields, Dictionary<string, int> index, string column) =>
        index.TryGetValue(column, out var i) && i < fields.Count && fields[i].Length > 0 ? fields[i] : null;

    private static double? Number(List<string> fields, Dictionary<string, int> index, string column) =>
        Field(fields, index, column) is { } text
            ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;

    /// <summary>Comma-separated fields; double quotes enclose a field that may contain commas ("" is a literal quote).</summary>
    private static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
