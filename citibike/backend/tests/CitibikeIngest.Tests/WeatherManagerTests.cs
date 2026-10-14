using CitibikeIngest.Api.Managers;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using CitibikeIngest.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace CitibikeIngest.Tests;

public sealed class WeatherManagerTests
{
    private const string Station = "USW00094728";

    private readonly FakeNoaaClient noaa = new();
    private readonly FakeWeatherRepository repository = new();
    private readonly WeatherOptions settings = new() { Station = Station, DefaultFrom = "2021-01-01", RefetchDays = 30, MaxDaysPerRequest = 3700 };

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private WeatherManager Manager() =>
        new(noaa, repository, Microsoft.Extensions.Options.Options.Create(settings), NullLogger<WeatherManager>.Instance);

    private static WeatherDay Day(DateOnly day, double? tmax = 20, double? prcp = 0) =>
        new(Station, day, tmax, tmax - 8, prcp, 0, 0, 2.1);

    private void NoaaHas(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            noaa.Days.Add(Day(d));
        }
    }

    // ---- range resolution -------------------------------------------------------------------

    [Fact]
    public async Task First_load_starts_at_the_default_and_runs_to_today()
    {
        var run = await Manager().LoadAsync(null, null, null, CancellationToken.None);

        Assert.Equal(WeatherRangeSource.Default, run.RangeSource);
        Assert.Equal(new DateOnly(2021, 1, 1), run.From);
        Assert.Equal(Today, run.To);
        Assert.Equal((Station, new DateOnly(2021, 1, 1), Today), Assert.Single(noaa.Calls));
    }

    [Fact]
    public async Task Next_load_refetches_the_last_days_so_noaa_corrections_arrive()
    {
        var lastDay = Today.AddDays(-10);
        repository.Stored[(Station, lastDay)] = Day(lastDay);

        var run = await Manager().LoadAsync(null, null, "cron", CancellationToken.None);

        Assert.Equal(WeatherRangeSource.LastDay, run.RangeSource);
        Assert.Equal(lastDay.AddDays(-30), run.From);
        Assert.Equal(Today, run.To);
        Assert.Equal("cron", run.Trigger);
    }

    [Fact]
    public async Task Explicit_dates_win()
    {
        var run = await Manager().LoadAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), null, CancellationToken.None);

        Assert.Equal(WeatherRangeSource.Parameter, run.RangeSource);
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)), (run.From, run.To));
        Assert.Equal(RunTrigger.Manual, run.Trigger);
    }

    [Fact]
    public async Task From_after_to_is_rejected_before_anything_is_recorded()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Manager().LoadAsync(new DateOnly(2026, 8, 31), new DateOnly(2026, 8, 1), null, CancellationToken.None));
        Assert.Empty(repository.Runs);
        Assert.Empty(noaa.Calls);
    }

    [Fact]
    public async Task A_future_end_date_is_rejected()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Manager().LoadAsync(Today, Today.AddDays(1), null, CancellationToken.None));
    }

    [Fact]
    public async Task A_range_longer_than_the_limit_is_rejected()
    {
        settings.MaxDaysPerRequest = 31;

        await Assert.ThrowsAsync<ValidationException>(() =>
            Manager().LoadAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), null, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_trigger_is_rejected()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Manager().LoadAsync(null, null, "nightly", CancellationToken.None));
    }

    // ---- loading ----------------------------------------------------------------------------

    [Fact]
    public async Task Load_stores_the_days_and_records_a_succeeded_run()
    {
        NoaaHas(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        var run = await Manager().LoadAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), null, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal((31, 31), (run.DaysReceived, run.DaysWritten));
        Assert.NotNull(run.FinishedAt);
        Assert.Null(run.Error);
        Assert.Equal(31, repository.Stored.Count);
    }

    [Fact]
    public async Task Loading_the_same_range_again_overwrites_instead_of_duplicating()
    {
        NoaaHas(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        var manager = Manager();
        await manager.LoadAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), null, CancellationToken.None);

        noaa.Days[0] = Day(new DateOnly(2026, 8, 1), tmax: 31.7); // NOAA corrected a value
        await manager.LoadAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), null, CancellationToken.None);

        Assert.Equal(31, repository.Stored.Count);
        Assert.Equal(31.7, repository.Stored[(Station, new DateOnly(2026, 8, 1))].TmaxC);
    }

    [Fact]
    public async Task Days_noaa_has_not_published_yet_are_simply_fewer_days_received()
    {
        NoaaHas(Today.AddDays(-20), Today.AddDays(-4)); // the last days are not out yet

        var run = await Manager().LoadAsync(Today.AddDays(-20), Today, null, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(17, run.DaysReceived);
    }

    [Fact]
    public async Task When_noaa_fails_the_run_is_recorded_as_failed_and_the_error_is_an_upstream_error()
    {
        noaa.FailWith = new HttpRequestException("Response status code does not indicate success: 503 (Service Unavailable).");

        var ex = await Assert.ThrowsAsync<UpstreamException>(() => Manager().LoadAsync(null, null, null, CancellationToken.None));

        var run = Assert.Single(repository.Runs);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.NotNull(run.FinishedAt);
        Assert.Contains("503", run.Error);
        Assert.Equal(run.Error, ex.Message);
        Assert.Empty(repository.Stored);
    }

    [Fact]
    public async Task An_unreadable_noaa_answer_is_an_upstream_error_too()
    {
        noaa.FailWith = new FormatException("Unexpected NOAA response, no STATION/DATE header: <html>");

        await Assert.ThrowsAsync<UpstreamException>(() => Manager().LoadAsync(null, null, null, CancellationToken.None));
        Assert.Equal(RunStatus.Failed, Assert.Single(repository.Runs).Status);
    }

    [Fact]
    public async Task A_load_while_another_is_running_conflicts()
    {
        repository.Runs.Add(new WeatherRun { Id = 1, Status = RunStatus.Running, StartedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<ConflictException>(() => Manager().LoadAsync(null, null, null, CancellationToken.None));
        Assert.Empty(noaa.Calls);
    }

    // ---- reading ----------------------------------------------------------------------------

    [Fact]
    public async Task Get_days_returns_the_stored_days_of_the_configured_station()
    {
        repository.Stored[(Station, new DateOnly(2026, 8, 2))] = Day(new DateOnly(2026, 8, 2));
        repository.Stored[(Station, new DateOnly(2026, 8, 1))] = Day(new DateOnly(2026, 8, 1));
        repository.Stored[("OTHER", new DateOnly(2026, 8, 1))] = Day(new DateOnly(2026, 8, 1)) with { Station = "OTHER" };

        var result = await Manager().GetDaysAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), CancellationToken.None);

        Assert.Equal(Station, result.Station);
        Assert.Equal([new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2)], result.Days.Select(d => d.Day));
    }

    [Fact]
    public async Task Get_days_validates_the_range()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Manager().GetDaysAsync(new DateOnly(2026, 8, 31), new DateOnly(2026, 8, 1), CancellationToken.None));
    }

    [Fact]
    public async Task Get_unknown_run_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Manager().GetRunAsync(42, CancellationToken.None));
    }
}
