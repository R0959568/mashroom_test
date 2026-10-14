using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests;

public class RemoteFileNameTests
{
    [Theory]
    [InlineData("2013-citibike-tripdata.zip", "nyc", 2013, null)]
    [InlineData("202401-citibike-tripdata.zip", "nyc", 2024, 1)]
    [InlineData("JC-202001-citibike-tripdata.csv.zip", "jc", 2020, 1)]
    [InlineData("JC-201708 citibike-tripdata.csv.zip", "jc", 2017, 8)]   // space instead of dash
    [InlineData("JC-202207-citbike-tripdata.csv.zip", "jc", 2022, 7)]    // typo in the bucket
    [InlineData("JC-202510-citibike-tripdata.zip", "jc", 2025, 10)]      // no .csv in the name
    public void Parses_every_naming_style_in_the_bucket(string name, string region, int year, int? month)
    {
        Assert.True(RemoteFileName.TryParse(name, out var r, out var y, out var m));
        Assert.Equal(region, r);
        Assert.Equal(year, y);
        Assert.Equal(month, m);
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("2013-citibike-tripdata.csv")]
    [InlineData("202401-citibike-tripdata.zip.bak")]
    public void Rejects_other_keys(string name)
    {
        Assert.False(RemoteFileName.TryParse(name, out _, out _, out _));
    }

    [Fact]
    public void Label_distinguishes_bundles_from_months()
    {
        Assert.Equal("2021 (yearly bundle)", TestData.Bundle2021.Label);
        Assert.Equal("2026-08", TestData.Aug2026.Label);
        Assert.True(TestData.Bundle2021.IsYearlyBundle);
    }
}
