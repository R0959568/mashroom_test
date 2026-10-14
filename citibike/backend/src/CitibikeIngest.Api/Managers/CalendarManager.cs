using System.ComponentModel;
using System.Text.Json;
using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Infrastructure;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Managers;

public sealed class CalendarManager(
    IHolidayExporter exporter,
    ICalendarRepository repository,
    IOptions<CalendarOptions> options,
    ILogger<CalendarManager> logger) : ICalendarManager
{
    private const int MaxErrorLength = 2000;
    private const int MinYear = 1900;
    private const int MaxYear = 2100;

    public async Task<CalendarRun> LoadAsync(int? fromYear, int? toYear, string? trigger, CancellationToken ct)
    {
        trigger = RunTrigger.Normalize(trigger);
        var settings = options.Value;
        var range = ResolveRange(fromYear, toYear);
        var categories = settings.CategoryList;
        if (categories.Count == 0)
        {
            throw new ValidationException("Calendar:Categories must name at least one category");
        }

        if (await repository.GetRunningRunAsync(ct) is { } running)
        {
            throw new ConflictException($"Calendar load {running.Id} is still in progress (started {running.StartedAt:u})");
        }

        var run = await repository.CreateRunAsync(new CalendarRun
        {
            Country = settings.Country,
            Subdivision = settings.Subdivision,
            Categories = string.Join(',', categories),
            FromYear = range.FromYear,
            ToYear = range.ToYear,
            RangeSource = range.Source,
            Trigger = trigger,
        }, ct);

        try
        {
            HolidayExport export;
            try
            {
                export = await exporter.ExportAsync(settings.Country, settings.Subdivision, range.FromYear, range.ToYear, categories, ct);
                run.Source = export.Source;
                run.SourceVersion = export.SourceVersion;
                run.HolidaysReceived = export.Holidays.Count;
                Validate(export, range);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException or Win32Exception)
            {
                // The exporter failed (non-zero exit, unreadable or invalid output) or Python could not be started
                // (Win32Exception): the source is unusable, the same class of failure as an unreachable NOAA.
                throw new UpstreamException($"Holiday export failed: {ex.Message}", ex);
            }

            run.HolidaysWritten = await repository.ReplaceHolidaysAsync(run.Id, settings.Country, settings.Subdivision, range, export, ct);
            run.Status = RunStatus.Succeeded;
            logger.LogInformation("Calendar load {RunId} ({Trigger}): {Country}-{Subdivision} {From}-{To} ({Source}), "
                                  + "{Package} {Version}, {Received} holiday(s) received, {Written} written",
                run.Id, trigger, run.Country, run.Subdivision, run.FromYear, run.ToYear, run.RangeSource,
                run.Source, run.SourceVersion, run.HolidaysReceived, run.HolidaysWritten);
            return run;
        }
        catch (UpstreamException ex)
        {
            logger.LogError(ex, "Calendar load {RunId}: holiday export failed", run.Id);
            run.Status = RunStatus.Failed;
            run.Error = Truncate(ex.Message);
            throw new UpstreamException(run.Error, ex.InnerException);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Calendar load {RunId} failed", run.Id);
            run.Status = RunStatus.Failed;
            run.Error = Truncate(ex.Message);
            throw;
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            await repository.UpdateRunAsync(run, CancellationToken.None);
        }
    }

    public async Task<CalendarRun> GetRunAsync(long id, CancellationToken ct) =>
        await repository.GetRunAsync(id, ct) ?? throw new NotFoundException($"Calendar load {id} does not exist");

    public async Task<IReadOnlyList<CalendarRun>> ListRunsAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 200)
        {
            throw new ValidationException("limit must be between 1 and 200");
        }

        return await repository.ListRunsAsync(limit, ct);
    }

    public async Task<HolidayDays> GetHolidaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from > to)
        {
            throw new ValidationException("from must be on or before to");
        }

        if (to.Year - from.Year + 1 > options.Value.MaxYearsPerRequest)
        {
            throw new ValidationException($"the range may span at most {options.Value.MaxYearsPerRequest} years");
        }

        var settings = options.Value;
        var holidays = await repository.GetHolidaysAsync(settings.Country, settings.Subdivision, from, to, ct);
        return new HolidayDays(settings.Country, settings.Subdivision, from, to, holidays);
    }

    /// <summary>
    /// Explicit years win; a missing one falls back to its default: Calendar:DefaultFromYear for the start, the
    /// current year (UTC) plus Calendar:YearsAhead for the end. Future years are allowed on purpose: the package
    /// computes them from rules, and a forecast for next year needs them.
    /// </summary>
    private CalendarRange ResolveRange(int? fromYear, int? toYear)
    {
        var settings = options.Value;
        var source = fromYear is null && toYear is null ? CalendarRangeSource.Default : CalendarRangeSource.Parameter;
        var range = new CalendarRange(
            fromYear ?? settings.DefaultFromYear,
            toYear ?? DateTime.UtcNow.Year + settings.YearsAhead,
            source);

        if (range.FromYear < MinYear || range.ToYear > MaxYear)
        {
            throw new ValidationException($"years must lie between {MinYear} and {MaxYear}");
        }

        if (range.FromYear > range.ToYear)
        {
            throw new ValidationException("fromYear must be on or before toYear");
        }

        if (range.ToYear - range.FromYear + 1 > settings.MaxYearsPerRequest)
        {
            throw new ValidationException($"a load may span at most {settings.MaxYearsPerRequest} years");
        }

        return range;
    }

    /// <summary>
    /// The load replaces the years it covers, so an empty or out-of-range export must fail instead of being
    /// written: it would wipe the stored holidays (the same lesson as an empty zip conversion, see convert_zip.py).
    /// </summary>
    private static void Validate(HolidayExport export, CalendarRange range)
    {
        if (export.Holidays.Count == 0)
        {
            throw new InvalidOperationException($"Holiday exporter returned no holidays for {range.FromYear}-{range.ToYear}");
        }

        if (export.Holidays.FirstOrDefault(h => h.Day < range.FirstDay || h.Day > range.LastDay) is { } outside)
        {
            throw new InvalidOperationException(
                $"Holiday exporter returned {outside.Day:yyyy-MM-dd} ({outside.Name}), outside {range.FromYear}-{range.ToYear}");
        }

        if (export.Holidays.GroupBy(h => (h.Day, h.Name, h.Category)).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidOperationException(
                $"Holiday exporter returned {duplicate.Key.Name} on {duplicate.Key.Day:yyyy-MM-dd} more than once");
        }
    }

    private static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
}
