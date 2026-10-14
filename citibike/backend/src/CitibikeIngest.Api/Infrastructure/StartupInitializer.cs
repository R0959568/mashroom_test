using CitibikeIngest.Api.Data;
using Npgsql;

namespace CitibikeIngest.Api.Infrastructure;

/// <summary>
/// Before the first request: create the metadata, weather and calendar tables if needed, fail ingest runs,
/// weather loads and calendar loads that a previous process left "running", and thereby warm the connection pool.
/// </summary>
public sealed class StartupInitializer(IServiceScopeFactory scopeFactory, ILogger<StartupInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.ReadOnlySqlTransaction or PostgresErrorCodes.InsufficientPrivilege)
        {
            // The notebooks' login (citibike/debug.sh) starts every transaction read-only and owns nothing in
            // citibike_meta: reads and the forecast work, starting a run does not. Production uses the ingest role.
            logger.LogWarning("Metadata schema not ensured ({SqlState}: {Message}); the login is read-only or lacks privileges. Reads and the forecast work, ingest, weather and calendar runs will fail. Expected with the debug login of citibike/debug.sh", ex.SqlState, ex.MessageText);
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IIngestRepository>();
        await repository.EnsureSchemaAsync(cancellationToken);
        var stale = await repository.MarkStaleRunsFailedAsync(cancellationToken);
        if (stale > 0)
        {
            logger.LogWarning("{Count} run(s) were still marked running from a previous process and are now failed", stale);
        }

        var weather = scope.ServiceProvider.GetRequiredService<IWeatherRepository>();
        var staleWeather = await weather.MarkStaleRunsFailedAsync(cancellationToken);
        if (staleWeather > 0)
        {
            logger.LogWarning("{Count} weather load(s) were still marked running from a previous process and are now failed", staleWeather);
        }

        var calendar = scope.ServiceProvider.GetRequiredService<ICalendarRepository>();
        var staleCalendar = await calendar.MarkStaleRunsFailedAsync(cancellationToken);
        if (staleCalendar > 0)
        {
            logger.LogWarning("{Count} calendar load(s) were still marked running from a previous process and are now failed", staleCalendar);
        }

        logger.LogInformation("Metadata schema ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
