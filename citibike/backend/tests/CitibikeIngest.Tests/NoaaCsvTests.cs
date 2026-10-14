using CitibikeIngest.Api.Data;

namespace CitibikeIngest.Tests;

public sealed class NoaaCsvTests
{
    // Real answer of the NCEI daily-summaries service for Central Park, 1-3 August 2026 (metric units).
    private const string Sample =
        "\"STATION\",\"DATE\",\"AWND\",\"PRCP\",\"SNOW\",\"SNWD\",\"TMAX\",\"TMIN\"\n" +
        "\"USW00094728\",\"2026-08-01\",\"1.6\",\"0.0\",\"0.0\",\"0.0\",\"30.0\",\"22.2\"\n" +
        "\"USW00094728\",\"2026-08-02\",\"1.5\",\"1.8\",\"0.0\",\"0.0\",\"28.3\",\"23.9\"\n" +
        "\"USW00094728\",\"2026-08-03\",\"2.1\",\"49.0\",\"0.0\",\"0.0\",\"26.7\",\"21.7\"\n";

    [Fact]
    public void Parses_every_day_with_its_values()
    {
        var days = NoaaCsv.Parse(Sample);

        Assert.Equal(3, days.Count);
        var rainy = days[2];
        Assert.Equal("USW00094728", rainy.Station);
        Assert.Equal(new DateOnly(2026, 8, 3), rainy.Day);
        Assert.Equal((26.7, 21.7, 49.0, 0.0, 0.0, 2.1),
            (rainy.TmaxC, rainy.TminC, rainy.PrcpMm, rainy.SnowMm, rainy.SnowDepthMm, rainy.WindMs));
    }

    [Fact]
    public void Columns_are_found_by_name_and_empty_fields_become_null()
    {
        const string csv =
            "\"STATION\",\"DATE\",\"TMAX\",\"TMIN\",\"PRCP\",\"AWND\"\r\n" +
            "\"USW00094728\",\"2021-03-14\",\"9.4\",\"-1.6\",\"0.0\",\r\n";   // CRLF, no wind, no snow columns at all

        var day = Assert.Single(NoaaCsv.Parse(csv));

        Assert.Equal((9.4, -1.6, 0.0), (day.TmaxC, day.TminC, day.PrcpMm));
        Assert.Null(day.WindMs);
        Assert.Null(day.SnowMm);
        Assert.Null(day.SnowDepthMm);
    }

    [Fact]
    public void A_quoted_field_may_contain_commas()
    {
        const string csv =
            "\"STATION\",\"NAME\",\"DATE\",\"TMAX\"\n" +
            "\"USW00094728\",\"NY CITY CENTRAL PARK, NY US\",\"2026-08-01\",\"30.0\"\n";

        Assert.Equal(30.0, Assert.Single(NoaaCsv.Parse(csv)).TmaxC);
    }

    [Fact]
    public void An_empty_body_means_no_days()
    {
        Assert.Empty(NoaaCsv.Parse(""));
        Assert.Empty(NoaaCsv.Parse("\n"));
    }

    [Fact]
    public void Anything_that_is_not_the_expected_csv_is_rejected()
    {
        var ex = Assert.Throws<FormatException>(() => NoaaCsv.Parse("{\"errorMessage\":\"Invalid parameter\"}"));
        Assert.Contains("STATION/DATE", ex.Message);
    }
}
