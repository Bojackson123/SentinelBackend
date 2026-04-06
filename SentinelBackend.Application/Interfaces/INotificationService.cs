namespace SentinelBackend.Application.Interfaces;

using SentinelBackend.Domain.Entities;

/// <summary>
/// Creates and manages notification incidents triggered by alarms.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Creates a notification incident for a newly raised alarm.
    /// Resolves the appropriate recipients based on the alarm's ownership chain.
    /// Schedules the initial delivery attempt(s).
    /// </summary>
    Task<NotificationIncident?> CreateIncidentForAlarmAsync(
        Alarm alarm,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges a notification incident, stopping further retries/escalation.
    /// </summary>
    Task<NotificationIncident?> AcknowledgeIncidentAsync(
        int incidentId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes all open notification incidents for a resolved alarm.
    /// </summary>
    Task<int> CloseIncidentsForAlarmAsync(
        int alarmId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Escalates a notification incident to the next level.
    /// Called by the dispatch worker when retries at the current level are exhausted.
    /// </summary>
    Task<NotificationIncident?> EscalateAsync(
        int incidentId,
        string reason,
        CancellationToken cancellationToken = default);
}
