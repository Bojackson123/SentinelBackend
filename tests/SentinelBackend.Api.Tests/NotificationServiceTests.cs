namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure;
using SentinelBackend.Tests.Shared;
using Xunit;

public class NotificationServiceTests
{
    [Fact]
    public async Task CreateIncident_ForNewAlarm_CreatesIncidentAndAttempt()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var service = new NotificationService(db, NullLogger<NotificationService>.Instance);

        var alarm = await CreateAlarmAsync(db, seed);

        var incident = await service.CreateIncidentForAlarmAsync(alarm);

        Assert.NotNull(incident);
        Assert.Equal(alarm.Id, incident!.AlarmId);
        Assert.Equal(NotificationIncidentStatus.Open, incident.Status);
        Assert.Equal(0, incident.CurrentEscalationLevel);

        var attempts = await db.NotificationAttempts
            .Where(a => a.NotificationIncidentId == incident.Id)
            .ToListAsync();
        Assert.Single(attempts);
        Assert.Equal(NotificationStatus.Pending, attempts[0].Status);
        Assert.Equal(1, attempts[0].AttemptNumber);
    }

    [Fact]
    public async Task CreateIncident_Duplicate_ReturnsSameIncident()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var service = new NotificationService(db, NullLogger<NotificationService>.Instance);
        var alarm = await CreateAlarmAsync(db, seed);

        var incident1 = await service.CreateIncidentForAlarmAsync(alarm);
        var incident2 = await service.CreateIncidentForAlarmAsync(alarm);

        Assert.NotNull(incident1);
        Assert.NotNull(incident2);
        Assert.Equal(incident1!.Id, incident2!.Id);
    }

    [Fact]
    public async Task AcknowledgeIncident_CancelsPendingAttempts()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var service = new NotificationService(db, NullLogger<NotificationService>.Instance);
        var alarm = await CreateAlarmAsync(db, seed);

        var incident = await service.CreateIncidentForAlarmAsync(alarm);
        Assert.NotNull(incident);

        var acked = await service.AcknowledgeIncidentAsync(incident!.Id, "user-1");

        Assert.NotNull(acked);
        Assert.Equal(NotificationIncidentStatus.Acknowledged, acked!.Status);
        Assert.NotNull(acked.AcknowledgedAt);
        Assert.Equal("user-1", acked.AcknowledgedByUserId);

        var pendingCount = await db.NotificationAttempts
            .CountAsync(a =>
                a.NotificationIncidentId == incident.Id
                && a.Status == NotificationStatus.Pending);
        Assert.Equal(0, pendingCount);
    }

    [Fact]
    public async Task CloseIncidents_ClosesOpenAndCancelsPending()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var service = new NotificationService(db, NullLogger<NotificationService>.Instance);
        var alarm = await CreateAlarmAsync(db, seed);

        await service.CreateIncidentForAlarmAsync(alarm);

        var closedCount = await service.CloseIncidentsForAlarmAsync(alarm.Id);

        Assert.Equal(1, closedCount);

        var incidents = await db.NotificationIncidents
            .Where(n => n.AlarmId == alarm.Id)
            .ToListAsync();
        Assert.All(incidents, i => Assert.Equal(NotificationIncidentStatus.Closed, i.Status));

        var pendingAttempts = await db.NotificationAttempts
            .CountAsync(a => a.Status == NotificationStatus.Pending);
        Assert.Equal(0, pendingAttempts);
    }

    [Fact]
    public async Task Escalate_IncrementsLevelAndCreatesEvent()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var service = new NotificationService(db, NullLogger<NotificationService>.Instance);
        var alarm = await CreateAlarmAsync(db, seed);

        var incident = await service.CreateIncidentForAlarmAsync(alarm);
        Assert.NotNull(incident);

        var escalated = await service.EscalateAsync(incident!.Id, "Max retries at level 0");

        Assert.NotNull(escalated);
        Assert.Equal(NotificationIncidentStatus.Escalated, escalated!.Status);
        Assert.Equal(1, escalated.CurrentEscalationLevel);

        var events = await db.EscalationEvents
            .Where(e => e.NotificationIncidentId == incident.Id)
            .ToListAsync();
        Assert.Single(events);
        Assert.Equal(0, events[0].FromLevel);
        Assert.Equal(1, events[0].ToLevel);
    }

    [Fact]
    public async Task Escalate_ClosedIncident_NoOp()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var service = new NotificationService(db, NullLogger<NotificationService>.Instance);
        var alarm = await CreateAlarmAsync(db, seed);

        var incident = await service.CreateIncidentForAlarmAsync(alarm);
        Assert.NotNull(incident);

        await service.CloseIncidentsForAlarmAsync(alarm.Id);

        // Reload incident
        var closed = await db.NotificationIncidents.FindAsync(incident!.Id);
        Assert.Equal(NotificationIncidentStatus.Closed, closed!.Status);

        var result = await service.EscalateAsync(incident.Id, "Should not escalate");
        Assert.Equal(NotificationIncidentStatus.Closed, result!.Status);
        Assert.Equal(0, result.CurrentEscalationLevel); // unchanged
    }

    [Fact]
    public async Task CreateIncident_PopulatesOwnershipFromAlarm()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        db.DeviceAssignments.Add(new DeviceAssignment
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            AssignedByUserId = "tech-1",
            AssignedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(
            db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (alarm, _) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        var notifService = new NotificationService(db, NullLogger<NotificationService>.Instance);
        var incident = await notifService.CreateIncidentForAlarmAsync(alarm);

        Assert.NotNull(incident);
        Assert.Equal(seed.Site.Id, incident!.SiteId);
        Assert.Equal(seed.Customer.Id, incident.CustomerId);
        Assert.Equal(seed.Company.Id, incident.CompanyId);
    }

    [Fact]
    public async Task AlarmService_RaiseAlarm_CreatesNotificationIncident()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var notifService = new NotificationService(db, NullLogger<NotificationService>.Instance);
        var alarmService = new AlarmService(
            db, NullLogger<AlarmService>.Instance, notifService);

        var (alarm, wasCreated) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        Assert.True(wasCreated);

        var incidents = await db.NotificationIncidents
            .Where(n => n.AlarmId == alarm.Id)
            .ToListAsync();
        Assert.Single(incidents);
        Assert.Equal(NotificationIncidentStatus.Open, incidents[0].Status);
    }

    [Fact]
    public async Task AlarmService_ResolveAlarm_ClosesNotificationIncident()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var notifService = new NotificationService(db, NullLogger<NotificationService>.Instance);
        var alarmService = new AlarmService(
            db, NullLogger<AlarmService>.Instance, notifService);

        var (alarm, _) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        await alarmService.AutoResolveAlarmsAsync(seed.Device.Id, "HighWater");

        var incidents = await db.NotificationIncidents
            .Where(n => n.AlarmId == alarm.Id)
            .ToListAsync();
        Assert.Single(incidents);
        Assert.Equal(NotificationIncidentStatus.Closed, incidents[0].Status);
    }

    private static async Task<Alarm> CreateAlarmAsync(
        Infrastructure.Persistence.SentinelDbContext db,
        SeedData seed)
    {
        var now = DateTime.UtcNow;
        var alarm = new Alarm
        {
            DeviceId = seed.Device.Id,
            CompanyId = seed.Company.Id,
            CustomerId = seed.Customer.Id,
            SiteId = seed.Site.Id,
            AlarmType = "HighWater",
            Severity = AlarmSeverity.Critical,
            Status = AlarmStatus.Active,
            SourceType = AlarmSourceType.TelemetryFallback,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Alarms.Add(alarm);
        await db.SaveChangesAsync();
        return alarm;
    }
}
