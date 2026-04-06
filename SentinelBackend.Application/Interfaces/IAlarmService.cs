namespace SentinelBackend.Application.Interfaces;

using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;

public interface IAlarmService
{
    /// <summary>
    /// Creates a new alarm if no active (Active/Acknowledged) alarm exists for the same device and alarm type.
    /// Returns the existing alarm if a duplicate is suppressed, or the new alarm.
    /// </summary>
    Task<(Alarm Alarm, bool WasCreated)> RaiseAlarmAsync(
        int deviceId,
        string alarmType,
        AlarmSeverity severity,
        AlarmSourceType sourceType,
        string? triggerMessageId = null,
        string? detailsJson = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an alarm by transitioning it to Resolved status.
    /// Only alarms in Active, Acknowledged, or Suppressed status can be resolved.
    /// </summary>
    Task<Alarm?> ResolveAlarmAsync(
        int alarmId,
        string? userId = null,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves all active alarms of a given type for a device (system-triggered auto-resolve).
    /// Returns the number of alarms resolved.
    /// </summary>
    Task<int> AutoResolveAlarmsAsync(
        int deviceId,
        string alarmType,
        string? reason = null,
        CancellationToken cancellationToken = default);
}
