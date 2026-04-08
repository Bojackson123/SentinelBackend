namespace SentinelBackend.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Notifications;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

public class NotificationService : INotificationService
{
    private readonly SentinelDbContext _db;
    private readonly ILogger<NotificationService> _logger;
    private readonly NotificationOptions _options;
    private readonly IMessagePublisher? _messagePublisher;

    // Default max attempts per escalation level — will be configurable via options
    private const int DefaultMaxAttempts = 3;

    public NotificationService(
        SentinelDbContext db,
        ILogger<NotificationService> logger,
        IOptions<NotificationOptions> options,
        IMessagePublisher? messagePublisher = null)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
        _messagePublisher = messagePublisher;
    }

    public async Task<NotificationIncident?> CreateIncidentForAlarmAsync(
        Alarm alarm,
        CancellationToken cancellationToken = default)
    {
        // Don't create duplicate open incidents for the same alarm
        var existingIncident = await _db.NotificationIncidents
            .FirstOrDefaultAsync(n =>
                n.AlarmId == alarm.Id
                && n.Status != NotificationIncidentStatus.Closed,
                cancellationToken);

        if (existingIncident is not null)
        {
            _logger.LogDebug(
                "Notification incident already open for alarm {AlarmId}",
                alarm.Id);
            return existingIncident;
        }

        var now = DateTime.UtcNow;
        var incident = new NotificationIncident
        {
            AlarmId = alarm.Id,
            DeviceId = alarm.DeviceId,
            SiteId = alarm.SiteId,
            CustomerId = alarm.CustomerId,
            CompanyId = alarm.CompanyId,
            Status = NotificationIncidentStatus.Open,
            CurrentEscalationLevel = 0,
            MaxAttempts = DefaultMaxAttempts,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.NotificationIncidents.Add(incident);

        // Schedule first-attempt deliveries. TestEmailRecipient / TestSmsRecipient override
        // placeholder addresses so the simulator can send real notifications without needing
        // real contact data on every device record.
        var attempts = new List<NotificationAttempt>();

        var emailRecipient = _options.TestEmailRecipient ?? ResolveInitialRecipient(alarm);
        if (!string.IsNullOrWhiteSpace(emailRecipient))
        {
            var emailAttempt = new NotificationAttempt
            {
                NotificationIncident = incident,
                Channel = NotificationChannel.Email,
                Status = NotificationStatus.Pending,
                Recipient = emailRecipient,
                AttemptNumber = 1,
                EscalationLevel = 0,
                ScheduledAt = now,
                CreatedAt = now,
            };
            _db.NotificationAttempts.Add(emailAttempt);
            attempts.Add(emailAttempt);
        }

        // SMS attempt — only created when a test/real recipient number is available.
        // In production this will come from the contact preferences lookup.
        var smsRecipient = _options.TestSmsRecipient;
        if (!string.IsNullOrWhiteSpace(smsRecipient))
        {
            var smsAttempt = new NotificationAttempt
            {
                NotificationIncident = incident,
                Channel = NotificationChannel.Sms,
                Status = NotificationStatus.Pending,
                Recipient = smsRecipient,
                AttemptNumber = 1,
                EscalationLevel = 0,
                ScheduledAt = now,
                CreatedAt = now,
            };
            _db.NotificationAttempts.Add(smsAttempt);
            attempts.Add(smsAttempt);
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Publish Service Bus messages for immediate dispatch
        if (_messagePublisher is not null)
        {
            foreach (var attempt in attempts)
            {
                await _messagePublisher.PublishAsync(
                    Workers.NotificationDispatchWorker.QueueName,
                    new Workers.NotificationMessage(attempt.Id),
                    cancellationToken: cancellationToken);
            }
        }

        _logger.LogInformation(
            "Notification incident {IncidentId} created for alarm {AlarmId} (device {DeviceId})",
            incident.Id, alarm.Id, alarm.DeviceId);

        return incident;
    }

    public async Task<NotificationIncident?> AcknowledgeIncidentAsync(
        int incidentId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var incident = await _db.NotificationIncidents.FindAsync([incidentId], cancellationToken);
        if (incident is null)
            return null;

        if (incident.Status is NotificationIncidentStatus.Closed
            or NotificationIncidentStatus.Acknowledged)
            return incident;

        var now = DateTime.UtcNow;
        incident.Status = NotificationIncidentStatus.Acknowledged;
        incident.AcknowledgedAt = now;
        incident.AcknowledgedByUserId = userId;
        incident.UpdatedAt = now;

        // Cancel any pending attempts
        var pendingAttempts = await _db.NotificationAttempts
            .Where(a =>
                a.NotificationIncidentId == incidentId
                && a.Status == NotificationStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var attempt in pendingAttempts)
        {
            attempt.Status = NotificationStatus.Cancelled;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Notification incident {IncidentId} acknowledged by {UserId}",
            incidentId, userId);

        return incident;
    }

    public async Task<int> CloseIncidentsForAlarmAsync(
        int alarmId,
        CancellationToken cancellationToken = default)
    {
        var openIncidents = await _db.NotificationIncidents
            .Where(n =>
                n.AlarmId == alarmId
                && n.Status != NotificationIncidentStatus.Closed)
            .ToListAsync(cancellationToken);

        if (openIncidents.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var incident in openIncidents)
        {
            incident.Status = NotificationIncidentStatus.Closed;
            incident.UpdatedAt = now;

            // Cancel any pending attempts
            var pendingAttempts = await _db.NotificationAttempts
                .Where(a =>
                    a.NotificationIncidentId == incident.Id
                    && a.Status == NotificationStatus.Pending)
                .ToListAsync(cancellationToken);

            foreach (var attempt in pendingAttempts)
            {
                attempt.Status = NotificationStatus.Cancelled;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Closed {Count} notification incident(s) for alarm {AlarmId}",
            openIncidents.Count, alarmId);

        return openIncidents.Count;
    }

    public async Task<NotificationIncident?> EscalateAsync(
        int incidentId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var incident = await _db.NotificationIncidents
            .Include(n => n.Alarm)
            .FirstOrDefaultAsync(n => n.Id == incidentId, cancellationToken);

        if (incident is null)
            return null;

        if (incident.Status is NotificationIncidentStatus.Closed
            or NotificationIncidentStatus.Acknowledged)
            return incident;

        var now = DateTime.UtcNow;
        var fromLevel = incident.CurrentEscalationLevel;
        var toLevel = fromLevel + 1;

        incident.CurrentEscalationLevel = toLevel;
        incident.Status = NotificationIncidentStatus.Escalated;
        incident.UpdatedAt = now;

        _db.EscalationEvents.Add(new EscalationEvent
        {
            NotificationIncident = incident,
            FromLevel = fromLevel,
            ToLevel = toLevel,
            Reason = reason,
            CreatedAt = now,
        });

        // Schedule next attempt at the new escalation level.
        // Recipient resolution for escalation levels is a placeholder.
        var recipient = ResolveEscalationRecipient(incident, toLevel);
        NotificationAttempt? escalationAttempt = null;
        if (recipient is not null)
        {
            escalationAttempt = new NotificationAttempt
            {
                NotificationIncident = incident,
                Channel = NotificationChannel.Email,
                Status = NotificationStatus.Pending,
                Recipient = recipient,
                AttemptNumber = 1,
                EscalationLevel = toLevel,
                ScheduledAt = now,
                CreatedAt = now,
            };
            _db.NotificationAttempts.Add(escalationAttempt);
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (_messagePublisher is not null && escalationAttempt is not null)
        {
            await _messagePublisher.PublishAsync(
                Workers.NotificationDispatchWorker.QueueName,
                new Workers.NotificationMessage(escalationAttempt.Id),
                cancellationToken: cancellationToken);
        }

        _logger.LogInformation(
            "Notification incident {IncidentId} escalated from level {FromLevel} to {ToLevel}: {Reason}",
            incidentId, fromLevel, toLevel, reason);

        return incident;
    }

    /// <summary>
    /// Placeholder: resolves the initial notification recipient from the alarm's ownership chain.
    /// Real implementation depends on tenant type, contact preferences, and notification policies.
    /// </summary>
    private static string? ResolveInitialRecipient(Alarm alarm)
    {
        // Phase 6 scaffolding — returns a placeholder.
        // Real implementation will:
        //   - For company-owned devices: notify the company admin contact
        //   - For homeowner devices: notify the customer email
        //   - Include internal staff for critical alarms
        return alarm.CompanyId.HasValue
            ? $"company-{alarm.CompanyId}@placeholder"
            : alarm.CustomerId.HasValue
                ? $"customer-{alarm.CustomerId}@placeholder"
                : null;
    }

    /// <summary>
    /// Placeholder: resolves the escalation recipient at a given level.
    /// </summary>
    private static string? ResolveEscalationRecipient(NotificationIncident incident, int level)
    {
        // Phase 6 scaffolding — escalation policy is not yet defined.
        // Level 0 = primary contact, Level 1 = secondary/manager, Level 2+ = internal ops
        return level switch
        {
            1 => incident.CompanyId.HasValue
                ? $"company-{incident.CompanyId}-manager@placeholder"
                : "internal-ops@placeholder",
            _ => "internal-ops@placeholder",
        };
    }
}
