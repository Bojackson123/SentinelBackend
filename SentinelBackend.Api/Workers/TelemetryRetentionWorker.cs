namespace SentinelBackend.Api.Workers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentinelBackend.Application;
using SentinelBackend.Infrastructure.Persistence;

/// <summary>
/// Background service that enforces telemetry retention policies.
///
/// Periodically purges TelemetryHistory rows older than the configured hot retention
/// window (default 90 days), provided they have been archived to blob storage.
/// Also purges old FailedIngressMessages.
///
/// Design doc §12: "90 days hot in Azure SQL; indefinite cold archive in Blob/ADLS"
/// </summary>
public class TelemetryRetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryRetentionWorker> _logger;
    private readonly RetentionOptions _options;
    private readonly TimeSpan _purgeInterval;

    public TelemetryRetentionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TelemetryRetentionWorker> logger,
        IOptions<RetentionOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;

        var intervalSeconds = _options.PurgeIntervalSeconds;
        if (intervalSeconds <= 0) intervalSeconds = 3600;
        _purgeInterval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TelemetryRetentionWorker started (hotDays={HotDays}, batchSize={BatchSize}, interval={IntervalSeconds}s)",
            _options.HotRetentionDays, _options.PurgeBatchSize, _purgeInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPurgeCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during retention purge cycle");
            }

            await Task.Delay(_purgeInterval, stoppingToken);
        }

        _logger.LogInformation("TelemetryRetentionWorker stopped");
    }

    /// <summary>
    /// Runs a single purge cycle. Exposed as internal for testability.
    /// </summary>
    internal async Task RunPurgeCycleAsync(CancellationToken stoppingToken = default)
    {
        var purged = await PurgeTelemetryAsync(stoppingToken);
        var failedPurged = await PurgeFailedIngressAsync(stoppingToken);

        if (purged > 0 || failedPurged > 0)
        {
            _logger.LogInformation(
                "Retention cycle: purged {TelemetryRows} telemetry rows, {FailedRows} failed ingress rows",
                purged, failedPurged);
        }
    }

    private async Task<int> PurgeTelemetryAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-_options.HotRetentionDays);

        var query = db.TelemetryHistory
            .Where(t => t.TimestampUtc < cutoff);

        // Optionally require archive before purge — safety check ensures
        // rows are only deleted if their raw payload has been archived to blob
        if (_options.RequireArchiveBeforePurge)
        {
            query = query.Where(t => t.RawPayloadBlobUri != null);
        }

        var rowsToDelete = await query
            .OrderBy(t => t.TimestampUtc)
            .Take(_options.PurgeBatchSize)
            .ToListAsync(stoppingToken);

        if (rowsToDelete.Count == 0)
            return 0;

        db.TelemetryHistory.RemoveRange(rowsToDelete);
        await db.SaveChangesAsync(stoppingToken);

        _logger.LogDebug(
            "Purged {Count} telemetry rows older than {Cutoff:u}",
            rowsToDelete.Count, cutoff);

        return rowsToDelete.Count;
    }

    private async Task<int> PurgeFailedIngressAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-_options.FailedIngressRetentionDays);

        var rowsToDelete = await db.FailedIngressMessages
            .Where(f => f.CreatedAt < cutoff)
            .OrderBy(f => f.CreatedAt)
            .Take(_options.PurgeBatchSize)
            .ToListAsync(stoppingToken);

        if (rowsToDelete.Count == 0)
            return 0;

        db.FailedIngressMessages.RemoveRange(rowsToDelete);
        await db.SaveChangesAsync(stoppingToken);

        _logger.LogDebug(
            "Purged {Count} failed ingress rows older than {Cutoff:u}",
            rowsToDelete.Count, cutoff);

        return rowsToDelete.Count;
    }
}
