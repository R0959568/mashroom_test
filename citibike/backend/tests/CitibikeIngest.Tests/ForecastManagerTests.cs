using CitibikeIngest.Api.Managers;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using CitibikeIngest.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace CitibikeIngest.Tests;

public sealed class ForecastManagerTests
{
    private const string Key = "6140.05";

    private readonly FakeForecastRepository repository = new();
    private readonly FakeForecastClient client = new();
    private readonly ForecastOptions settings = new() { Url = "https://example.invalid", ApiKey = "k", MinHistoryDays = 28 };

    private static readonly DateOnly LastDay = new(2026, 8, 31);

    public ForecastManagerTests()
    {
        repository.Stations[Key] = new StationSummary(Key, "W 21 St & 6 Ave", 40.74, -73.99, 500_000);
    }

    private ForecastManager Manager() =>
        new(repository, client, Microsoft.Extensions.Options.Options.Create(settings), NullLogger<ForecastManager>.Instance);

    /// <summary>A full row of 05's feature table for a Tuesday in August with 300 days of history and a year earlier.</summary>
    private static FeatureRow Row(DateOnly day, FeatureSource source = FeatureSource.Table, int historyDays = 300, int? actual = 640, int? actualRest = 9_800,
        double? mtd = 610.5, int? restLy = 9_000, int? sum28 = 16_800, int? sum28Ly = 15_000) => new()
    {
        StationKey = Key,
        Day = day,
        Source = source,
        HistoryDays = historyDays,
        DaysInHorizon = 31 - day.Day + 1,
        ActualDay = actual,
        ActualRestOfMonth = actualRest,
        DepLag1 = 630,
        DepLag7 = 650,
        DepLag14 = 600,
        DepLag28 = 580,
        DepMean7 = 612.3,
        DepMean4Weeks = 615.0,
        DepMtd = mtd,
        DepSum28 = sum28,
        MemberShare28 = 0.82,
        EbikeShare28 = 0.55,
        ArrLag1 = 620,
        ArrMean7 = 605.7,
        DepRestOfMonthLy = restLy,
        DepSum28Ly = sum28Ly,
        IsoDow = 2,
        Month = 8,
        DayOfMonth = day.Day,
        Holiday = false,
        DayOff = false,
        HolidayLag7 = true,
        TmaxC = 29.4,
        TminC = 21.1,
        PrcpMm = 0.0,
        SnowMm = 0.0,
        SnowDepthMm = 0.0,
        WindMs = 3.2,
        WeatherDay = day,
    };

    // ---- the model inputs: modelling.design() and model_inputs() of the notebooks --------------------------------

    [Fact]
    public void Inputs_follow_the_notebooks_design()
    {
        var row = Row(new DateOnly(2026, 8, 11));

        var (day, rest) = ModelInputs.Build(row);

        Assert.Equal(Key, day["station_key"]);
        Assert.Equal(630, day["dep_lag_1"]);
        Assert.Equal(615.0, day["dep_mean_4_weeks"]);
        Assert.Equal(0, day["holiday"]);
        Assert.Equal(1, day["holiday_lag_7"]);
        Assert.Equal(29.4, day["tmax_c"]);
        Assert.Equal(1.0, day["scale"]);
        Assert.False(day.ContainsKey("days_in_horizon"));

        Assert.Equal(21, rest["days_in_horizon"]);
        Assert.Equal(11, rest["day_of_month"]);
        Assert.Equal(610.5, rest["mtd_rate"]);
        Assert.Equal(9_000 * (16_800.0 / 15_000) / 21, (double)rest["last_year_rate"], 6);
        Assert.Equal(1, rest["has_last_year"]);
        Assert.Equal(21.0, rest["scale"]);
        Assert.False(rest.ContainsKey("tmax_c"));
    }

    [Fact]
    public void Inputs_fall_back_like_the_notebooks_when_the_rates_are_missing()
    {
        var first = Row(new DateOnly(2026, 8, 1), mtd: null, restLy: null, sum28Ly: null);
        var (_, rest) = ModelInputs.Build(first);
        Assert.Equal(612.3, rest["mtd_rate"]);          // the 1st of the month: last week's mean
        Assert.Equal(612.3, rest["last_year_rate"]);    // no year earlier: last week's mean
        Assert.Equal(0, rest["has_last_year"]);
        Assert.Equal(31.0, rest["scale"]);

        var emptyLastYear = Row(new DateOnly(2026, 8, 11), sum28Ly: 0);
        var (_, rest2) = ModelInputs.Build(emptyLastYear);
        Assert.Equal(0, rest2["has_last_year"]);        // a growth ratio over zero is not finite

        var noShares = Row(new DateOnly(2026, 8, 11)) with { MemberShare28 = null, EbikeShare28 = null };
        var (day, _) = ModelInputs.Build(noShares);
        Assert.Equal(0.0, day["member_share_28"]);
    }

    [Fact]
    public void Inputs_refuse_a_row_without_the_lags()
    {
        var row = Row(new DateOnly(2026, 8, 11)) with { DepLag28 = null };
        var ex = Assert.Throws<ValidationException>(() => ModelInputs.Build(row));
        Assert.Contains("dep_lag_28", ex.Message);
    }

    // ---- the forecast ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_day_inside_the_data_uses_the_table_row_and_returns_the_actuals()
    {
        var day = new DateOnly(2026, 8, 11);
        repository.TableRows[(Key, day)] = Row(day);

        var forecast = await Manager().ForecastAsync(Key, day, CancellationToken.None);

        Assert.Equal(12.3, forecast.Day.Prediction);
        Assert.Equal(640, forecast.Day.Actual);
        Assert.Equal(345.6, forecast.RestOfMonth.Prediction);
        Assert.Equal(9_800, forecast.RestOfMonth.Actual);
        Assert.Equal(21, forecast.RestOfMonth.DaysInHorizon);
        Assert.Equal("lightgbm", forecast.Day.Model.Family);
        Assert.Empty(repository.ComputeCalls);
        Assert.Equal([Horizon.Day, Horizon.RestOfMonth], client.Calls.Select(c => c.Horizon));
        Assert.Equal(1.0, client.Calls[0].Row["scale"]);
        Assert.Equal(21.0, client.Calls[1].Row["scale"]);
    }

    [Fact]
    public async Task The_day_after_the_data_is_computed_and_has_no_actuals()
    {
        var tomorrow = LastDay.AddDays(1);
        repository.ComputedRows[Key] = Row(tomorrow, FeatureSource.Computed, actual: null, actualRest: null) with { DaysInHorizon = 30, Month = 9 };

        var forecast = await Manager().ForecastAsync(Key, tomorrow, CancellationToken.None);

        Assert.Equal((Key, tomorrow), Assert.Single(repository.ComputeCalls));
        Assert.Null(forecast.Day.Actual);
        Assert.Null(forecast.RestOfMonth.Actual);
        Assert.Equal(30, forecast.RestOfMonth.DaysInHorizon);
        Assert.Equal(FeatureSource.Computed, forecast.Inputs.Source);
    }

    [Fact]
    public async Task Days_outside_the_range_are_refused_before_any_call()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Manager().ForecastAsync(Key, LastDay.AddDays(2), CancellationToken.None));
        Assert.Contains("2026-09-01", ex.Message);
        await Assert.ThrowsAsync<ValidationException>(() => Manager().ForecastAsync(Key, new DateOnly(2020, 12, 31), CancellationToken.None));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task A_cold_station_is_refused_with_the_rule_of_05()
    {
        var day = new DateOnly(2026, 8, 11);
        repository.TableRows[(Key, day)] = Row(day, historyDays: 12);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Manager().ForecastAsync(Key, day, CancellationToken.None));

        Assert.Contains("12 day(s)", ex.Message);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task An_inactive_station_day_and_an_unknown_station_are_refused()
    {
        var day = new DateOnly(2026, 8, 11);
        await Assert.ThrowsAsync<ValidationException>(() => Manager().ForecastAsync(Key, day, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => Manager().ForecastAsync("0000.00", day, CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_configured_api_the_feature_answers_not_configured()
    {
        settings.Url = "";
        var day = new DateOnly(2026, 8, 11);
        repository.TableRows[(Key, day)] = Row(day);

        await Assert.ThrowsAsync<NotConfiguredException>(() => Manager().ForecastAsync(Key, day, CancellationToken.None));
    }

    [Fact]
    public async Task An_api_failure_is_passed_on_as_upstream()
    {
        var day = new DateOnly(2026, 8, 11);
        repository.TableRows[(Key, day)] = Row(day);
        client.Failure = new UpstreamException("The forecast API answered 502: cold");

        await Assert.ThrowsAsync<UpstreamException>(() => Manager().ForecastAsync(Key, day, CancellationToken.None));
    }

    [Fact]
    public async Task Stations_are_listed_busiest_first_and_the_limit_is_checked()
    {
        repository.Stations["0001.01"] = new StationSummary("0001.01", "Quiet Pl & 1 Ave", null, null, 10);

        var stations = await Manager().ListStationsAsync(" ave ", 10, CancellationToken.None);

        Assert.Equal([Key, "0001.01"], stations.Select(s => s.StationKey));
        await Assert.ThrowsAsync<ValidationException>(() => Manager().ListStationsAsync(null, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Map_covers_the_models_28_days_up_to_today_and_names_tomorrow()
    {
        repository.MapStations.Add(new MapStation(Key, "W 21 St & 6 Ave", 40.74, -73.99, 581.3, 28));

        var map = await Manager().GetMapAsync(LastDay, CancellationToken.None);

        Assert.Equal(LastDay, map.Today);
        Assert.Equal(LastDay.AddDays(1), map.Tomorrow);
        Assert.Equal(28, map.WindowDays);
        Assert.Equal([(LastDay, 28)], repository.MapCalls);
        Assert.Single(map.Stations);
    }

    [Theory]
    [InlineData(1)]   // the day after the data: nothing observed on it yet
    [InlineData(-2100)] // before the data (2020-11-29)
    public async Task Map_rejects_a_today_outside_the_data(int daysFromLastDay)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Manager().GetMapAsync(LastDay.AddDays(daysFromLastDay), CancellationToken.None));

        Assert.Contains("2026-08-31", ex.Message);
        Assert.Empty(repository.MapCalls);
    }
}
