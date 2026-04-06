namespace SentinelBackend.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

public class AlarmService : IAlarmService
{
    private readonly SentinelDbContext _db;
    private readonly ILogger<AlarmService> _logger;

    public AlarmService(SentinelDbContext db, ILogger<AlarmService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<(Alarm Alarm, bool WasCreated)> RaiseAlarmAsync(
        int deviceId,
        string alarmType,
        AlarmSeverity severity,
        AlarmSourceType sourceType,
        string? triggerMessageId = null,
        string? detailsJson = null,
        CancellationToken cancellationToken = default)
    {
        // Duplicate active incident suppression: do not create a new alarm if one
        // already exists in Active or Acknowledged status for the same device+type.
        var existingAlarm = await _db.Alarms
            .FirstOrDefaultAsync(a =>
                a.DeviceId == deviceId
                && a.AlarmType == alarmType
                && (a.Status == AlarmStatus.Active || a.Status == AlarmStatus.Acknowledged),
                cancellationToken);

        if (existingAlarm is not null)
        {
            _logger.LogDebug(
                "Duplicate alarm suppressed: {AlarmType} on device {DeviceId} — existing alarm {AlarmId}",
                alarmType, deviceId, existingAlarm.Id);
            return (existingAlarm, false);
        }

        // Resolve ownership snapshot from active assignment
        var ownership = await _db.DeviceAssignments
            .Where(a => a.DeviceId == deviceId && a.UnassignedAt == null)
            .Select(a => new { a.Id, a.SiteId, a.Site.CustomerId, a.Site.Customer.CompanyId })
            .FirstOrDefaultAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var alarm = new Alarm
        {
            DeviceId = deviceId,
            DeviceAssignmentId = ownership?.Id,
            SiteId = ownership?.SiteId,
            CustomerId = ownership?.CustomerId,
            CompanyId = ownership?.CompanyId,
            AlarmType = alarmType,
            Severity = severity,
            Status = AlarmStatus.Active,
            SourceType = sourceType,
            TriggerMessageId = triggerMessageId,
            DetailsJson = detailsJson,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Alarms.Add(alarm);

        _db.AlarmEvents.Add(new AlarmEvent
        {
            Alarm = alarm,
            EventType = "Created",
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Alarm raised: {AlarmType} (severity={Severity}) on device {DeviceId} — alarm {AlarmId}",
            alarmType, severity, deviceId, alarm.Id);

        return (alarm, true);
    }

    public async Task<Alarm?> ResolveAlarmAsync(
        int alarmId,
        string? userId = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var alarm = await _db.Alarms.FindAsync([alarmId], cancellationToken);
        if (alarm is null)
            return null;

        if (alarm.Status == AlarmStatus.Resolved)
            return alarm;

        var now = DateTime.UtcNow;
        alarm.Status = AlarmStatus.Resolved;
        alarm.ResolvedAt = now;
        alarm.UpdatedAt = now;

        _db.AlarmEvents.Add(new AlarmEvent
        {
            AlarmId = alarm.Id,
            EventType = "Resolved",
            UserId = userId,
            Reason = reason,
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Alarm {AlarmId} resolved", alarmId);
        return alarm;
    }

    public async Task<int> AutoResolveAlarmsAsync(
        int deviceId,
        string alarmType,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var activeAlarms = await _db.Alarms
            .Where(a =>
                a.DeviceId == deviceId
                && a.AlarmType == alarmType
                && a.Status != AlarmStatus.Resolved)
            .ToListAsync(cancellationToken);

        if (activeAlarms.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var alarm in activeAlarms)
        {
            alarm.Status = AlarmStatus.Resolved;
            alarm.ResolvedAt = now;
            alarm.UpdatedAt = now;

            _db.AlarmEvents.Add(new AlarmEvent
            {
                AlarmId = alarm.Id,
                EventType = "Resolved",
                Reason = reason ?? "Auto-resolved: condition cleared",
                CreatedAt = now,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Auto-resolved {Count} {AlarmType} alarm(s) for device {DeviceId}",
            activeAlarms.Count, alarmType, deviceId);

        return activeAlarms.Count;
    }
}
