using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Managers;

public sealed class WeatherManager(
    INoaaClient noaa,
    IWeatherRepository repository,
    IOptions<WeatherOptions> options,
    ILogger<WeatherManager> logger) : IWeatherManager
{
    private const int MaxErrorLength = 2000;

    public async Task<WeatherRun> LoadAsync(DateOnly? from, DateOnly? to, string? trigger, CancellationToken ct)
    {
        trigger = RunTrigger.Normalize(trigger);
        var settings = options.Value;
        var range = await ResolveRangeAsync(from, to, ct);

        if (await repository.GetRunningRunAsync(ct) is { } running)
        {
            throw new ConflictException($"Weather load {running.Id} is still in progress (started {running.StartedAt:u})");
        }

        var run = await repository.CreateRunAsync(new WeatherRun
        {
            Station = settings.Station,
            From = range.From,
            To = range.To,
            RangeSource = range.Source,
            Trigger = trigger,
        }, ct);

        try
        {
            var days = await noaa.GetDailyAsync(settings.Station, range.From, range.To, ct);
            run.DaysReceived = days.Count;
            run.DaysWritten = await repository.UpsertDaysAsync(run.Id, days, ct);
            run.Status = RunStatus.Succeeded;
            logger.LogInformation("Weather load {RunId} ({Trigger}): {Station} {From:yyyy-MM-dd} -> {To:yyyy-MM-dd} ({Source}), "
                                  + "{Received} day(s) received, {Written} written",
                run.Id, trigger, run.Station, run.From, run.To, run.RangeSource, run.DaysReceived, run.DaysWritten);
            return run;
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // HttpRequestException: NOAA unreachable or an error status; TaskCanceledException without our own
            // cancellation: the HttpClient timeout; FormatException: NOAA answered with something we cannot parse.
            logger.LogError(ex, "Weather load {RunId}: NOAA request failed", run.Id);
            run.Status = RunStatus.Failed;
            run.Error = Truncate($"NOAA request failed: {ex.Message}");
            throw new UpstreamException(run.Error, ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Weather load {RunId} failed", run.Id);
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

    public async Task<WeatherRun> GetRunAsync(long id, CancellationToken ct) =>
        await repository.GetRunAsync(id, ct) ?? throw new NotFoundException($"Weather load {id} does not exist");

    public async Task<IReadOnlyList<WeatherRun>> ListRunsAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 200)
        {
            throw new ValidationException("limit must be between 1 and 200");
        }

        return await repository.ListRunsAsync(limit, ct);
    }

    public async Task<WeatherDays> GetDaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        ValidateRange(from, to);
        var station = options.Value.Station;
        return new WeatherDays(station, from, to, await repository.GetDaysAsync(station, from, to, ct));
    }

    /// <summary>
    /// Explicit dates win. Without a start date the load begins RefetchDays before the last stored day, because
    /// NOAA publishes with a delay of days and corrects recent values; with nothing stored yet it starts at
    /// DefaultFrom. Without an end date it runs up to today (UTC); days NOAA has not published are just absent.
    /// </summary>
    private async Task<WeatherRange> ResolveRangeAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var settings = options.Value;
        var end = to ?? Today();
        WeatherRange range;
        if (from is { } explicitFrom)
        {
            range = new WeatherRange(explicitFrom, end, WeatherRangeSource.Parameter);
        }
        else if (await repository.GetLastDayAsync(settings.Station, ct) is { } lastDay)
        {
            var start = lastDay.AddDays(-settings.RefetchDays);
            range = new WeatherRange(start < end ? start : end, end, WeatherRangeSource.LastDay);
        }
        else
        {
            range = new WeatherRange(settings.DefaultFromDate, end, WeatherRangeSource.Default);
        }

        if (range.To > Today())
        {
            throw new ValidationException("to cannot be in the future");
        }

        ValidateRange(range.From, range.To);
        return range;
    }

    private void ValidateRange(DateOnly from, DateOnly to)
    {
        if (from > to)
        {
            throw new ValidationException("from must be on or before to");
        }

        var max = options.Value.MaxDaysPerRequest;
        if (to.DayNumber - from.DayNumber + 1 > max)
        {
            throw new ValidationException($"the range may span at most {max} days");
        }
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
}
