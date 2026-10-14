using System.ComponentModel;
using CitibikeIngest.Api.Managers;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using CitibikeIngest.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace CitibikeIngest.Tests;

public sealed class CalendarManagerTests
{
    private readonly FakeHolidayExporter exporter = new();
    private readonly FakeCalendarRepository repository = new();
    private readonly CalendarOptions settings = new()
    {
        Country = "US", Subdivision = "NY", Categories = "public,unofficial", DefaultFromYear = 2021, YearsAhead = 2, MaxYearsPerRequest = 50,
    };

    private static int ThisYear => DateTime.UtcNow.Year;

    private CalendarManager Manager() =>
        new(exporter, repository, Microsoft.Extensions.Options.Options.Create(settings), NullLogger<CalendarManager>.Instance);

    // Real package output for Christmas 2022 (a Sunday), see tests/test_export_holidays.py.
    private static readonly Holiday ChristmasEve2022 = new(new DateOnly(2022, 12, 24), "Christmas Eve", "unofficial", false);
    private static readonly Holiday Christmas2022 = new(new DateOnly(2022, 12, 25), "Christmas Day", "public", false);
    private static readonly Holiday ChristmasObserved2022 = new(new DateOnly(2022, 12, 26), "Christmas Day (observed)", "public", true);
    private static readonly Holiday Thanksgiving2024 = new(new DateOnly(2024, 11, 28), "Thanksgiving Day", "public", false);

    // ---- range resolution -------------------------------------------------------------------

    [Fact]
    public async Task Without_years_the_load_runs_from_the_default_year_to_years_ahead()
    {
        exporter.Holidays.Add(Christmas2022);

        var run = await Manager().LoadAsync(null, null, null, CancellationToken.None);

        Assert.Equal(CalendarRangeSource.Default, run.RangeSource);
        Assert.Equal((2021, ThisYear + 2), (run.FromYear, run.ToYear));
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(("US", "NY", 2021, ThisYear + 2), (call.Country, call.Subdivision, call.FromYear, call.ToYear));
        Assert.Equal(["public", "unofficial"], call.Categories);
    }

    [Fact]
    public async Task Explicit_years_win_and_a_missing_one_falls_back_to_its_default()
    {
        exporter.Holidays.Add(Thanksgiving2024);

        var run = await Manager().LoadAsync(2024, null, "cron", CancellationToken.None);

        Assert.Equal(CalendarRangeSource.Parameter, run.RangeSource);
        Assert.Equal((2024, ThisYear + 2), (run.FromYear, run.ToYear));
        Assert.Equal("cron", run.Trigger);
    }

    [Theory]
    [InlineData(2025, 2024)]  // reversed
    [InlineData(1899, 2024)]  // before the supported years
    [InlineData(2024, 2101)]  // after them
    [InlineData(2000, 2060)]  // more than MaxYearsPerRequest
    public async Task Bad_years_are_rejected_before_anything_is_recorded(int fromYear, int toYear)
    {
        await Assert.ThrowsAsync<ValidationException>(() => Manager().LoadAsync(fromYear, toYear, null, CancellationToken.None));
        Assert.Empty(repository.Runs);
        Assert.Empty(exporter.Calls);
    }

    [Fact]
    public async Task An_unknown_trigger_is_rejected()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Manager().LoadAsync(null, null, "nightly", CancellationToken.None));
    }

    // ---- loading ----------------------------------------------------------------------------

    [Fact]
    public async Task Load_stores_the_rows_unchanged_and_records_the_package_version()
    {
        exporter.Holidays.AddRange([ChristmasEve2022, Christmas2022, ChristmasObserved2022]);

        var run = await Manager().LoadAsync(2022, 2022, null, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal((3, 3), (run.HolidaysReceived, run.HolidaysWritten));
        Assert.Equal(("holidays", "0.105"), (run.Source, run.SourceVersion));
        Assert.Equal("public,unofficial", run.Categories);
        Assert.NotNull(run.FinishedAt);
        Assert.Null(run.Error);
        Assert.Equal([ChristmasEve2022, Christmas2022, ChristmasObserved2022], repository.Stored.Select(s => s.Holiday.Holiday));
        Assert.All(repository.Stored, s => Assert.Equal(("US", "NY", "0.105"), (s.Country, s.Subdivision, s.Holiday.SourceVersion)));
    }

    [Fact]
    public async Task Reloading_replaces_the_years_so_a_dropped_holiday_does_not_linger()
    {
        exporter.Holidays.AddRange([ChristmasEve2022, Christmas2022, ChristmasObserved2022]);
        var manager = Manager();
        await manager.LoadAsync(2022, 2022, null, CancellationToken.None);

        exporter.Holidays.Remove(ChristmasEve2022); // a newer package version no longer lists it
        exporter.Version = "0.106";
        await manager.LoadAsync(2022, 2022, null, CancellationToken.None);

        Assert.Equal([Christmas2022, ChristmasObserved2022], repository.Stored.Select(s => s.Holiday.Holiday));
        Assert.All(repository.Stored, s => Assert.Equal("0.106", s.Holiday.SourceVersion));
    }

    [Fact]
    public async Task Reloading_one_year_leaves_the_other_years_alone()
    {
        exporter.Holidays.AddRange([Christmas2022, Thanksgiving2024]);
        var manager = Manager();
        await manager.LoadAsync(2022, 2024, null, CancellationToken.None);

        exporter.Holidays.Remove(Christmas2022);
        await manager.LoadAsync(2024, 2024, null, CancellationToken.None);

        Assert.Equal([Christmas2022, Thanksgiving2024], repository.Stored.Select(s => s.Holiday.Holiday));
    }

    [Fact]
    public async Task An_empty_export_fails_and_keeps_the_stored_holidays()
    {
        exporter.Holidays.Add(Christmas2022);
        var manager = Manager();
        await manager.LoadAsync(2022, 2022, null, CancellationToken.None);

        exporter.Holidays.Clear();
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => manager.LoadAsync(2022, 2022, null, CancellationToken.None));

        Assert.Contains("no holidays", ex.Message);
        Assert.Equal(RunStatus.Failed, repository.Runs[^1].Status);
        Assert.Equal([Christmas2022], repository.Stored.Select(s => s.Holiday.Holiday));
    }

    [Fact]
    public async Task A_holiday_outside_the_requested_years_fails_the_load()
    {
        exporter.ReturnExactly = [Christmas2022, Thanksgiving2024];

        var ex = await Assert.ThrowsAsync<UpstreamException>(() => Manager().LoadAsync(2022, 2022, null, CancellationToken.None));

        Assert.Contains("2024-11-28", ex.Message);
        Assert.Empty(repository.Stored);
    }

    [Fact]
    public async Task A_duplicate_row_fails_the_load_instead_of_a_primary_key_violation()
    {
        exporter.ReturnExactly = [Christmas2022, Christmas2022];

        await Assert.ThrowsAsync<UpstreamException>(() => Manager().LoadAsync(2022, 2022, null, CancellationToken.None));
        Assert.Empty(repository.Stored);
    }

    [Fact]
    public async Task When_the_exporter_fails_the_run_is_recorded_as_failed_and_the_error_is_an_upstream_error()
    {
        exporter.FailWith = new InvalidOperationException("Holiday exporter exited with code 1: export_holidays: No module named 'holidays'");

        var ex = await Assert.ThrowsAsync<UpstreamException>(() => Manager().LoadAsync(null, null, null, CancellationToken.None));

        var run = Assert.Single(repository.Runs);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.NotNull(run.FinishedAt);
        Assert.Contains("No module named 'holidays'", run.Error);
        Assert.Equal(run.Error, ex.Message);
    }

    [Fact]
    public async Task A_missing_python_is_an_upstream_error_too()
    {
        exporter.FailWith = new Win32Exception(2, "The system cannot find the file specified");

        await Assert.ThrowsAsync<UpstreamException>(() => Manager().LoadAsync(null, null, null, CancellationToken.None));
        Assert.Equal(RunStatus.Failed, Assert.Single(repository.Runs).Status);
    }

    [Fact]
    public async Task A_load_while_another_is_running_conflicts()
    {
        repository.Runs.Add(new CalendarRun { Id = 1, Status = RunStatus.Running, StartedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<ConflictException>(() => Manager().LoadAsync(null, null, null, CancellationToken.None));
        Assert.Empty(exporter.Calls);
    }

    // ---- reading ----------------------------------------------------------------------------

    [Fact]
    public async Task Get_holidays_returns_the_stored_rows_of_the_configured_region_in_the_range()
    {
        exporter.Holidays.AddRange([ChristmasEve2022, Christmas2022, ChristmasObserved2022, Thanksgiving2024]);
        await Manager().LoadAsync(2022, 2024, null, CancellationToken.None);
        repository.Stored.Add(("US", "CA", new StoredHoliday(Christmas2022, "holidays", "0.105")));

        var result = await Manager().GetHolidaysAsync(new DateOnly(2022, 12, 25), new DateOnly(2022, 12, 31), CancellationToken.None);

        Assert.Equal(("US", "NY"), (result.Country, result.Subdivision));
        Assert.Equal([Christmas2022, ChristmasObserved2022], result.Holidays.Select(h => h.Holiday));
    }

    [Fact]
    public async Task Get_holidays_validates_the_range()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Manager().GetHolidaysAsync(new DateOnly(2022, 12, 31), new DateOnly(2022, 12, 1), CancellationToken.None));
    }

    [Fact]
    public async Task Get_unknown_run_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Manager().GetRunAsync(42, CancellationToken.None));
    }
}
