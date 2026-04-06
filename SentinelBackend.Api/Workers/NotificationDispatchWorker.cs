namespace SentinelBackend.Api.Workers;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

/// <summary>
/// Background service that processes pending notification attempts.
///
/// Periodically scans for NotificationAttempts in Pending status,
/// dispatches them through the configured INotificationDispatcher,
/// and updates attempt status. Handles retries and triggers escalation
/// when max attempts at a level are exhausted.
/// </summary>
public class NotificationDispatchWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDispatchWorker> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;

    public NotificationDispatchWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationDispatchWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var pollSeconds = configuration.GetValue<int?>("NotificationDispatch:PollIntervalSeconds") ?? 30;
        if (pollSeconds <= 0) pollSeconds = 30;
        _pollInterval = TimeSpan.FromSeconds(pollSeconds);

        _batchSize = configuration.GetValue<int?>("NotificationDispatch:BatchSize") ?? 50;
        if (_batchSize <= 0) _batchSize = 50;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "NotificationDispatchWorker started (poll={PollSeconds}s, batch={BatchSize})",
            _pollInterval.TotalSeconds, _batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAttemptsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during notification dispatch cycle");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("NotificationDispatchWorker stopped");
    }

    private async Task ProcessPendingAttemptsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var now = DateTime.UtcNow;

        // Fetch pending attempts that are due for sending
        var pendingAttempts = await db.NotificationAttempts
            .Include(a => a.NotificationIncident)
                .ThenInclude(n => n.Alarm)
            .Where(a =>
                a.Status == NotificationStatus.Pending
                && a.ScheduledAt <= now)
            .OrderBy(a => a.ScheduledAt)
            .Take(_batchSize)
            .ToListAsync(stoppingToken);

        if (pendingAttempts.Count == 0)
            return;

        _logger.LogDebug("Processing {Count} pending notification attempt(s)", pendingAttempts.Count);

        foreach (var attempt in pendingAttempts)
        {
            if (stoppingToken.IsCancellationRequested) break;

            // Skip if the incident was already closed/acknowledged
            if (attempt.NotificationIncident.Status is NotificationIncidentStatus.Closed
                or NotificationIncidentStatus.Acknowledged)
            {
                attempt.Status = NotificationStatus.Cancelled;
                continue;
            }

            attempt.Status = NotificationStatus.Sending;
            attempt.SentAt = DateTime.UtcNow;

            try
            {
                var result = await dispatcher.SendAsync(
                    attempt, attempt.NotificationIncident.Alarm, stoppingToken);

                if (result.Accepted)
                {
                    attempt.Status = NotificationStatus.Delivered;
                    attempt.DeliveredAt = DateTime.UtcNow;
                    attempt.ProviderMessageId = result.ProviderMessageId;

                    _logger.LogInformation(
                        "Notification attempt {AttemptId} delivered to {Recipient} via {Channel}",
                        attempt.Id, attempt.Recipient, attempt.Channel);
                }
                else
                {
                    HandleFailedAttempt(attempt, result.ErrorMessage);
                    await TryScheduleRetryOrEscalateAsync(
                        db, notificationService, attempt, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception sending notification attempt {AttemptId}",
                    attempt.Id);

                HandleFailedAttempt(attempt, ex.Message);
                await TryScheduleRetryOrEscalateAsync(
                    db, notificationService, attempt, stoppingToken);
            }
        }

        await db.SaveChangesAsync(stoppingToken);
    }

    private void HandleFailedAttempt(Domain.Entities.NotificationAttempt attempt, string? errorMessage)
    {
        attempt.Status = NotificationStatus.Failed;
        attempt.FailedAt = DateTime.UtcNow;
        attempt.ErrorMessage = errorMessage is { Length: > 2000 }
            ? errorMessage[..2000]
            : errorMessage;

        _logger.LogWarning(
            "Notification attempt {AttemptId} failed: {Error}",
            attempt.Id, errorMessage);
    }

    private async Task TryScheduleRetryOrEscalateAsync(
        SentinelDbContext db,
        INotificationService notificationService,
        Domain.Entities.NotificationAttempt failedAttempt,
        CancellationToken stoppingToken)
    {
        var incident = failedAttempt.NotificationIncident;

        // Count attempts at the current escalation level
        var attemptsAtLevel = await db.NotificationAttempts
            .CountAsync(a =>
                a.NotificationIncidentId == incident.Id
                && a.EscalationLevel == failedAttempt.EscalationLevel
                && a.Status != NotificationStatus.Pending,
                stoppingToken);

        if (attemptsAtLevel < incident.MaxAttempts)
        {
            // Schedule a retry with exponential backoff
            var delay = TimeSpan.FromMinutes(Math.Pow(2, failedAttempt.AttemptNumber));
            var scheduledAt = DateTime.UtcNow.Add(delay);

            db.NotificationAttempts.Add(new Domain.Entities.NotificationAttempt
            {
                NotificationIncidentId = incident.Id,
                Channel = failedAttempt.Channel,
                Status = NotificationStatus.Pending,
                Recipient = failedAttempt.Recipient,
                AttemptNumber = failedAttempt.AttemptNumber + 1,
                EscalationLevel = failedAttempt.EscalationLevel,
                ScheduledAt = scheduledAt,
                CreatedAt = DateTime.UtcNow,
            });

            _logger.LogDebug(
                "Retry scheduled for incident {IncidentId} at {ScheduledAt} (attempt #{Attempt})",
                incident.Id, scheduledAt, failedAttempt.AttemptNumber + 1);
        }
        else
        {
            // Max retries exhausted at this level — escalate
            _logger.LogInformation(
                "Max retries exhausted for incident {IncidentId} at level {Level}, escalating",
                incident.Id, failedAttempt.EscalationLevel);

            try
            {
                await notificationService.EscalateAsync(
                    incident.Id,
                    $"Max retries ({incident.MaxAttempts}) exhausted at escalation level {failedAttempt.EscalationLevel}",
                    stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to escalate notification incident {IncidentId}",
                    incident.Id);
            }
        }
    }
}
