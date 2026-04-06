namespace SentinelBackend.Tests.Shared;

using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;

/// <summary>
/// No-op notification service for tests that don't need notification behavior.
/// </summary>
public class NullNotificationService : INotificationService
{
    public Task<NotificationIncident?> CreateIncidentForAlarmAsync(
        Alarm alarm, CancellationToken cancellationToken = default)
        => Task.FromResult<NotificationIncident?>(null);

    public Task<NotificationIncident?> AcknowledgeIncidentAsync(
        int incidentId, string userId, CancellationToken cancellationToken = default)
        => Task.FromResult<NotificationIncident?>(null);

    public Task<int> CloseIncidentsForAlarmAsync(
        int alarmId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<NotificationIncident?> EscalateAsync(
        int incidentId, string reason, CancellationToken cancellationToken = default)
        => Task.FromResult<NotificationIncident?>(null);
}
