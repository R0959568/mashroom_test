using CitibikeIngest.Api.Managers.Interfaces;

namespace CitibikeIngest.Api.Infrastructure;

/// <summary>
/// Executes queued runs one at a time. Downloads and conversions take minutes to hours, far beyond
/// any request timeout, so POST returns immediately and the UI / cron polls GET /api/ingest/runs/{id}.
/// </summary>
public sealed class IngestWorker(IIngestQueue queue, IServiceScopeFactory scopeFactory, ILogger<IngestWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = scopeFactory.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IIngestManager>();
            try
            {
                await manager.ExecuteRunAsync(runId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Ingest worker stopping; run {RunId} was interrupted", runId);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run {RunId} crashed outside the run's own error handling", runId);
            }
        }
    }
}
