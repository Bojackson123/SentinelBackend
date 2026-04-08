namespace SentinelBackend.Infrastructure.Workers;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

/// <summary>
/// Background service that processes pending notification attempts.
///
/// When Azure Service Bus is configured, listens on the "notifications" queue for
/// instant dispatch. Falls back to database polling when Service Bus is unavailable.
/// </summary>
public class NotificationDispatchWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDispatchWorker> _logger;
    private readonly ServiceBusClient? _serviceBusClient;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;

    public const string QueueName = "notifications";

    public NotificationDispatchWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationDispatchWorker> logger,
        IConfiguration configuration,
        ServiceBusClient? serviceBusClient = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _serviceBusClient = serviceBusClient;

        var pollSeconds = configuration.GetValue<int?>("NotificationDispatch:PollIntervalSeconds") ?? 30;
        if (pollSeconds <= 0) pollSeconds = 30;
        _pollInterval = TimeSpan.FromSeconds(pollSeconds);

        _batchSize = configuration.GetValue<int?>("NotificationDispatch:BatchSize") ?? 50;
        if (_batchSize <= 0) _batchSize = 50;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_serviceBusClient is not null)
        {
            _logger.LogInformation("NotificationDispatchWorker started in Service Bus mode (queue={Queue})", QueueName);
            await RunServiceBusModeAsync(stoppingToken);
        }
        else
        {
            _logger.LogInformation(
                "NotificationDispatchWorker started in polling mode (poll={PollSeconds}s, batch={BatchSize})",
                _pollInterval.TotalSeconds, _batchSize);
            await RunPollingModeAsync(stoppingToken);
        }

        _logger.LogInformation("NotificationDispatchWorker stopped");
    }

    private async Task RunServiceBusModeAsync(CancellationToken stoppingToken)
    {
        await using var processor = _serviceBusClient!.CreateProcessor(QueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 10,
        });

        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                var body = args.Message.Body.ToString();
                var envelope = JsonSerializer.Deserialize<NotificationMessage>(body);
                if (envelope is { AttemptId: > 0 })
                {
                    await ProcessSingleAttemptAsync(envelope.AttemptId, args.CancellationToken);
                }
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Service Bus notification message");
            }
        };

        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Service Bus processor error (source={Source})", args.ErrorSource);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }

        await processor.StopProcessingAsync();
    }

    private async Task RunPollingModeAsync(CancellationToken stoppingToken)
    {
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
    }

    /// <summary>
    /// Processes a single notification attempt by ID. Used by the Service Bus handler.
    /// Exposed as internal for testability.
    /// </summary>
    internal async Task ProcessSingleAttemptAsync(long attemptId, CancellationToken stoppingToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var dispatchers = scope.ServiceProvider.GetRequiredService<IEnumerable<INotificationDispatcher>>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var attempt = await db.NotificationAttempts
            .Include(a => a.NotificationIncident)
                .ThenInclude(n => n.Alarm)
            .FirstOrDefaultAsync(a => a.Id == attemptId && a.Status == NotificationStatus.Pending, stoppingToken);

        if (attempt is null)
        {
            _logger.LogDebug("Notification attempt {AttemptId} not found or no longer Pending, skipping", attemptId);
            return;
        }

        var dispatcherByChannel = dispatchers
            .GroupBy(d => d.Channel)
            .ToDictionary(g => g.Key, g => g.Last());

        await DispatchAttemptAsync(db, notificationService, dispatcherByChannel, attempt, stoppingToken);
        await db.SaveChangesAsync(stoppingToken);
    }

    private async Task ProcessPendingAttemptsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var dispatchers = scope.ServiceProvider.GetRequiredService<IEnumerable<INotificationDispatcher>>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var now = DateTime.UtcNow;

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

        // Build a lookup keyed by channel — last registration wins per channel if duplicates exist
        var dispatcherByChannel = dispatchers
            .GroupBy(d => d.Channel)
            .ToDictionary(g => g.Key, g => g.Last());

        foreach (var attempt in pendingAttempts)
        {
            if (stoppingToken.IsCancellationRequested) break;

            await DispatchAttemptAsync(db, notificationService, dispatcherByChannel, attempt, stoppingToken);
        }

        await db.SaveChangesAsync(stoppingToken);
    }

    private async Task DispatchAttemptAsync(
        SentinelDbContext db,
        INotificationService notificationService,
        Dictionary<NotificationChannel, INotificationDispatcher> dispatcherByChannel,
        Domain.Entities.NotificationAttempt attempt,
        CancellationToken stoppingToken)
    {
        if (attempt.NotificationIncident.Status is NotificationIncidentStatus.Closed
            or NotificationIncidentStatus.Acknowledged)
        {
            attempt.Status = NotificationStatus.Cancelled;
            return;
        }

        if (!dispatcherByChannel.TryGetValue(attempt.Channel, out var dispatcher))
        {
            _logger.LogWarning(
                "No dispatcher registered for channel {Channel} — cancelling attempt {AttemptId}",
                attempt.Channel, attempt.Id);
            attempt.Status = NotificationStatus.Cancelled;
            attempt.ErrorMessage = $"No dispatcher registered for channel {attempt.Channel}";
            return;
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

        var attemptsAtLevel = await db.NotificationAttempts
            .CountAsync(a =>
                a.NotificationIncidentId == incident.Id
                && a.EscalationLevel == failedAttempt.EscalationLevel
                && a.Status != NotificationStatus.Pending,
                stoppingToken);

        if (attemptsAtLevel < incident.MaxAttempts)
        {
            var delay = TimeSpan.FromMinutes(Math.Pow(2, failedAttempt.AttemptNumber));
            var scheduledAt = DateTime.UtcNow.Add(delay);

            var retryAttempt = new Domain.Entities.NotificationAttempt
            {
                NotificationIncidentId = incident.Id,
                Channel = failedAttempt.Channel,
                Status = NotificationStatus.Pending,
                Recipient = failedAttempt.Recipient,
                AttemptNumber = failedAttempt.AttemptNumber + 1,
                EscalationLevel = failedAttempt.EscalationLevel,
                ScheduledAt = scheduledAt,
                CreatedAt = DateTime.UtcNow,
            };

            db.NotificationAttempts.Add(retryAttempt);
            await db.SaveChangesAsync(stoppingToken);

            // If Service Bus is available, schedule the retry message with delay
            var publisher = GetMessagePublisher();
            if (publisher is not null)
            {
                await publisher.PublishAsync(
                    QueueName,
                    new NotificationMessage(retryAttempt.Id),
                    new DateTimeOffset(scheduledAt, TimeSpan.Zero),
                    stoppingToken);
            }

            _logger.LogDebug(
                "Retry scheduled for incident {IncidentId} at {ScheduledAt} (attempt #{Attempt})",
                incident.Id, scheduledAt, failedAttempt.AttemptNumber + 1);
        }
        else
        {
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

    private IMessagePublisher? GetMessagePublisher()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            return scope.ServiceProvider.GetService<IMessagePublisher>();
        }
        catch
        {
            return null;
        }
    }
}

public record NotificationMessage(long AttemptId);
