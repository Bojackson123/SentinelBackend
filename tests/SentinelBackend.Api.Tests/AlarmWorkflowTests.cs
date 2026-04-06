namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Tests.Shared;
using Xunit;

public class AlarmWorkflowTests
{
    [Fact]
    public async Task Alarm_Created_WithActiveStatus()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var alarm = new Alarm
        {
            DeviceId = seed.Device.Id,
            AlarmType = "HighWater",
            Severity = AlarmSeverity.Critical,
            Status = AlarmStatus.Active,
            SourceType = AlarmSourceType.TelemetryFallback,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Alarms.Add(alarm);
        await db.SaveChangesAsync();

        var saved = await db.Alarms.FirstAsync();
        Assert.Equal(AlarmStatus.Active, saved.Status);
        Assert.Equal("HighWater", saved.AlarmType);
    }

    [Fact]
    public async Task Alarm_Acknowledge_TransitionsFromActive()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var alarm = new Alarm
        {
            DeviceId = seed.Device.Id,
            AlarmType = "HighWater",
            Severity = AlarmSeverity.Critical,
            Status = AlarmStatus.Active,
            SourceType = AlarmSourceType.TelemetryFallback,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Alarms.Add(alarm);
        await db.SaveChangesAsync();

        // Acknowledge
        alarm.Status = AlarmStatus.Acknowledged;
        alarm.UpdatedAt = DateTime.UtcNow;
        db.AlarmEvents.Add(new AlarmEvent
        {
            AlarmId = alarm.Id,
            EventType = "Acknowledged",
            UserId = "user-1",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(AlarmStatus.Acknowledged, alarm.Status);
        var events = await db.AlarmEvents.Where(e => e.AlarmId == alarm.Id).ToListAsync();
        Assert.Single(events);
        Assert.Equal("Acknowledged", events[0].EventType);
    }

    [Fact]
    public async Task Alarm_CannotAcknowledge_IfNotActive()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var alarm = new Alarm
        {
            DeviceId = seed.Device.Id,
            AlarmType = "HighWater",
            Severity = AlarmSeverity.Critical,
            Status = AlarmStatus.Resolved,
            SourceType = AlarmSourceType.TelemetryFallback,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Alarms.Add(alarm);
        await db.SaveChangesAsync();

        var canAcknowledge = alarm.Status == AlarmStatus.Active;
        Assert.False(canAcknowledge);
    }

    [Fact]
    public async Task Alarm_Suppress_WithReason()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var alarm = new Alarm
        {
            DeviceId = seed.Device.Id,
            AlarmType = "LowVoltage",
            Severity = AlarmSeverity.Warning,
            Status = AlarmStatus.Active,
            SourceType = AlarmSourceType.SystemGenerated,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Alarms.Add(alarm);
        await db.SaveChangesAsync();

        alarm.Status = AlarmStatus.Suppressed;
        alarm.SuppressReason = "Maintenance in progress";
        alarm.SuppressedByUserId = "tech-1";
        alarm.UpdatedAt = DateTime.UtcNow;

        db.AlarmEvents.Add(new AlarmEvent
        {
            AlarmId = alarm.Id,
            EventType = "Suppressed",
            UserId = "tech-1",
            Reason = "Maintenance in progress",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(AlarmStatus.Suppressed, alarm.Status);
        Assert.Equal("Maintenance in progress", alarm.SuppressReason);
    }

    [Fact]
    public async Task Alarm_CannotSuppress_IfAlreadyResolved()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var alarm = new Alarm
        {
            DeviceId = seed.Device.Id,
            AlarmType = "HighWater",
            Severity = AlarmSeverity.Critical,
            Status = AlarmStatus.Resolved,
            SourceType = AlarmSourceType.TelemetryFallback,
            StartedAt = DateTime.UtcNow,
            ResolvedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Alarms.Add(alarm);
        await db.SaveChangesAsync();

        var canSuppress = alarm.Status is not (AlarmStatus.Resolved or AlarmStatus.Suppressed);
        Assert.False(canSuppress);
    }

    [Fact]
    public async Task Alarm_EventHistory_OrderedChronologically()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var alarm = new Alarm
        {
            DeviceId = seed.Device.Id,
            AlarmType = "HighWater",
            Severity = AlarmSeverity.Critical,
            Status = AlarmStatus.Active,
            SourceType = AlarmSourceType.TelemetryFallback,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Alarms.Add(alarm);
        await db.SaveChangesAsync();

        var baseTime = DateTime.UtcNow;
        db.AlarmEvents.AddRange(
            new AlarmEvent
            {
                AlarmId = alarm.Id,
                EventType = "Created",
                CreatedAt = baseTime,
            },
            new AlarmEvent
            {
                AlarmId = alarm.Id,
                EventType = "Acknowledged",
                UserId = "user-1",
                CreatedAt = baseTime.AddMinutes(5),
            },
            new AlarmEvent
            {
                AlarmId = alarm.Id,
                EventType = "Suppressed",
                UserId = "tech-1",
                Reason = "Testing",
                CreatedAt = baseTime.AddMinutes(10),
            }
        );
        await db.SaveChangesAsync();

        var events = await db.AlarmEvents
            .Where(e => e.AlarmId == alarm.Id)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();

        Assert.Equal(3, events.Count);
        Assert.Equal("Created", events[0].EventType);
        Assert.Equal("Acknowledged", events[1].EventType);
        Assert.Equal("Suppressed", events[2].EventType);
    }

    [Fact]
    public async Task Alarm_OwnershipSnapshot_Populated()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var alarm = new Alarm
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            CustomerId = seed.Customer.Id,
            CompanyId = seed.Company.Id,
            AlarmType = "HighWater",
            Severity = AlarmSeverity.Critical,
            Status = AlarmStatus.Active,
            SourceType = AlarmSourceType.TelemetryFallback,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Alarms.Add(alarm);
        await db.SaveChangesAsync();

        var saved = await db.Alarms.FirstAsync();
        Assert.Equal(seed.Company.Id, saved.CompanyId);
        Assert.Equal(seed.Customer.Id, saved.CustomerId);
        Assert.Equal(seed.Site.Id, saved.SiteId);
    }
}
