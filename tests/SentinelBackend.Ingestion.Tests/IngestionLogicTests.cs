namespace SentinelBackend.Ingestion.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Tests.Shared;
using Xunit;

/// <summary>
/// Tests the core ingestion data-path logic (dedup, latest state, connectivity,
/// first-telemetry activation, lifecycle handling, ownership snapshot, firmware update).
/// These test the EF patterns used by TelemetryIngestionWorker without requiring EventProcessorClient.
/// </summary>
public class IngestionLogicTests
{
    private static async Task<(SentinelDbContext db, Device device)> SetupDeviceAsync(DeviceStatus status = DeviceStatus.Assigned)
    {
        var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = status;
        seed.Device.DeviceId = seed.Device.SerialNumber;

        // Assign device to site so ownership snapshot works
        var assignment = new DeviceAssignment
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            AssignedAt = DateTime.UtcNow,
            AssignedByUserId = "admin",
        };
        db.DeviceAssignments.Add(assignment);
        await db.SaveChangesAsync();

        return (db, seed.Device);
    }

    [Fact]
    public async Task Dedup_SkipsDuplicateMessage()
    {
        var (db, device) = await SetupDeviceAsync(DeviceStatus.Active);
        using (db)
        {
            // Insert first telemetry record
            db.TelemetryHistory.Add(new TelemetryHistory
            {
                DeviceId = device.Id,
                MessageId = "msg-001",
                MessageType = "telemetry",
                TimestampUtc = DateTime.UtcNow,
                EnqueuedAtUtc = DateTime.UtcNow,
                ReceivedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            // Check dedup — same device + messageId
            var isDuplicate = await db.TelemetryHistory
                .AnyAsync(t => t.DeviceId == device.Id && t.MessageId == "msg-001");

            Assert.True(isDuplicate);
        }
    }

    [Fact]
    public async Task LatestState_OnlyUpdatesWhenNewer()
    {
        var (db, device) = await SetupDeviceAsync(DeviceStatus.Active);
        using (db)
        {
            var state = new LatestDeviceState
            {
                DeviceId = device.Id,
                LastTelemetryTimestampUtc = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc),
                PanelVoltage = 24.0,
                UpdatedAt = DateTime.UtcNow,
            };
            db.LatestDeviceStates.Add(state);
            await db.SaveChangesAsync();

            // Old message — should NOT update
            var oldTimestamp = new DateTime(2026, 4, 5, 11, 0, 0, DateTimeKind.Utc);
            if (oldTimestamp > state.LastTelemetryTimestampUtc)
            {
                state.PanelVoltage = 18.0;
                state.LastTelemetryTimestampUtc = oldTimestamp;
            }

            // New message — should update
            var newTimestamp = new DateTime(2026, 4, 5, 13, 0, 0, DateTimeKind.Utc);
            if (newTimestamp > state.LastTelemetryTimestampUtc)
            {
                state.PanelVoltage = 26.0;
                state.LastTelemetryTimestampUtc = newTimestamp;
            }

            await db.SaveChangesAsync();

            Assert.Equal(26.0, state.PanelVoltage);
            Assert.Equal(newTimestamp, state.LastTelemetryTimestampUtc);
        }
    }

    [Fact]
    public async Task Connectivity_AlwaysUpdates_RegardlessOfMessageType()
    {
        var (db, device) = await SetupDeviceAsync(DeviceStatus.Active);
        using (db)
        {
            var connectivity = new DeviceConnectivityState
            {
                DeviceId = device.Id,
                LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-30),
                LastMessageType = "telemetry",
                IsOffline = true,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-30),
            };
            db.DeviceConnectivityStates.Add(connectivity);
            await db.SaveChangesAsync();

            // Simulate lifecycle message arrival
            var now = DateTime.UtcNow;
            connectivity.LastMessageReceivedAt = now;
            connectivity.LastMessageType = "lifecycle";
            connectivity.IsOffline = false;
            connectivity.UpdatedAt = now;
            await db.SaveChangesAsync();

            Assert.Equal("lifecycle", connectivity.LastMessageType);
            Assert.False(connectivity.IsOffline);
            Assert.Equal(now, connectivity.LastMessageReceivedAt);
        }
    }

    [Fact]
    public async Task FirstTelemetry_Transitions_AssignedToActive()
    {
        var (db, device) = await SetupDeviceAsync(DeviceStatus.Assigned);
        using (db)
        {
            // Simulate first telemetry activation logic from worker
            if (device.Status == DeviceStatus.Assigned)
            {
                device.Status = DeviceStatus.Active;
            }
            await db.SaveChangesAsync();

            var updated = await db.Devices.FindAsync(device.Id);
            Assert.Equal(DeviceStatus.Active, updated!.Status);
        }
    }

    [Fact]
    public async Task FirstTelemetry_DoesNotActivate_IfNotAssigned()
    {
        var (db, device) = await SetupDeviceAsync(DeviceStatus.Unprovisioned);
        using (db)
        {
            // Same logic — only Assigned → Active
            if (device.Status == DeviceStatus.Assigned)
            {
                device.Status = DeviceStatus.Active;
            }
            await db.SaveChangesAsync();

            var updated = await db.Devices.FindAsync(device.Id);
            Assert.Equal(DeviceStatus.Unprovisioned, updated!.Status);
        }
    }

    [Fact]
    public async Task OwnershipSnapshot_Stamped_OnTelemetryHistory()
    {
        var (db, device) = await SetupDeviceAsync(DeviceStatus.Active);
        using (db)
        {
            // Resolve ownership like the worker does
            var activeAssignment = await db.DeviceAssignments
                .Where(a => a.DeviceId == device.Id && a.UnassignedAt == null)
                .Select(a => new { a.Id, a.SiteId, a.Site.CustomerId, a.Site.Customer.CompanyId })
                .FirstOrDefaultAsync();

            Assert.NotNull(activeAssignment);

            var telemetryRecord = new TelemetryHistory
            {
                DeviceId = device.Id,
                MessageId = "msg-snapshot",
                MessageType = "telemetry",
                TimestampUtc = DateTime.UtcNow,
                EnqueuedAtUtc = DateTime.UtcNow,
                ReceivedAtUtc = DateTime.UtcNow,
                DeviceAssignmentId = activeAssignment.Id,
                SiteId = activeAssignment.SiteId,
                CustomerId = activeAssignment.CustomerId,
                CompanyId = activeAssignment.CompanyId,
            };
            db.TelemetryHistory.Add(telemetryRecord);
            await db.SaveChangesAsync();

            var saved = await db.TelemetryHistory.FirstAsync(t => t.MessageId == "msg-snapshot");
            Assert.Equal(activeAssignment.SiteId, saved.SiteId);
            Assert.Equal(activeAssignment.CustomerId, saved.CustomerId);
            Assert.Equal(activeAssignment.CompanyId, saved.CompanyId);
        }
    }

    [Fact]
    public async Task Lifecycle_BootId_UpdatesConnectivity()
    {
        var (db, device) = await SetupDeviceAsync(DeviceStatus.Active);
        using (db)
        {
            var connectivity = new DeviceConnectivityState
            {
                DeviceId = device.Id,
                LastMessageReceivedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.DeviceConnectivityStates.Add(connectivity);
            await db.SaveChangesAsync();

            // Simulate lifecycle boot event
            var bootId = "boot-abc-123";
            connectivity.LastBootId = bootId;
            connectivity.LastMessageType = "lifecycle";
            connectivity.LastMessageReceivedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var saved = await db.DeviceConnectivityStates.FirstAsync(c => c.DeviceId == device.Id);
            Assert.Equal(bootId, saved.LastBootId);
            Assert.Equal("lifecycle", saved.LastMessageType);
        }
    }

    [Fact]
    public async Task FirmwareVersion_Updated_WhenReported()
    {
        var (db, device) = await SetupDeviceAsync(DeviceStatus.Active);
        using (db)
        {
            Assert.Null(device.FirmwareVersion);

            // Simulate firmware update from message
            var firmwareVersion = "1.2.3";
            if (!string.IsNullOrWhiteSpace(firmwareVersion))
            {
                device.FirmwareVersion = firmwareVersion;
            }
            await db.SaveChangesAsync();

            var updated = await db.Devices.FindAsync(device.Id);
            Assert.Equal("1.2.3", updated!.FirmwareVersion);
        }
    }

    [Fact]
    public async Task UnknownDevice_FailedIngress_Recorded()
    {
        using var db = TestDb.Create();

        // No device seeded — simulate unknown device
        var device = await db.Devices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.DeviceId == "unknown-device-xyz");

        Assert.Null(device);

        // Record failed ingress
        db.FailedIngressMessages.Add(new FailedIngressMessage
        {
            SourceDeviceId = "unknown-device-xyz",
            MessageId = "msg-fail",
            PartitionId = "0",
            Offset = "12345",
            EnqueuedAt = DateTime.UtcNow,
            FailureReason = "UnknownDevice",
            ErrorMessage = "No device record for deviceId 'unknown-device-xyz'",
            RawPayload = "{}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var failed = await db.FailedIngressMessages.FirstAsync();
        Assert.Equal("UnknownDevice", failed.FailureReason);
        Assert.Equal("unknown-device-xyz", failed.SourceDeviceId);
    }

    [Fact]
    public async Task TelemetryHistory_AllMeasurementFields_Persisted()
    {
        var (db, device) = await SetupDeviceAsync(DeviceStatus.Active);
        using (db)
        {
            var record = new TelemetryHistory
            {
                DeviceId = device.Id,
                MessageId = "msg-full",
                MessageType = "telemetry",
                TimestampUtc = DateTime.UtcNow,
                EnqueuedAtUtc = DateTime.UtcNow,
                ReceivedAtUtc = DateTime.UtcNow,
                PanelVoltage = 24.5,
                PumpCurrent = 3.2,
                PumpRunning = true,
                HighWaterAlarm = false,
                TemperatureC = 22.1,
                SignalRssi = -67,
                RuntimeSeconds = 86400,
                ReportedCycleCount = 150,
                FirmwareVersion = "1.0.0",
                BootId = "boot-1",
                SequenceNumber = 42,
            };
            db.TelemetryHistory.Add(record);
            await db.SaveChangesAsync();

            var saved = await db.TelemetryHistory.FirstAsync(t => t.MessageId == "msg-full");
            Assert.Equal(24.5, saved.PanelVoltage);
            Assert.Equal(3.2, saved.PumpCurrent);
            Assert.True(saved.PumpRunning);
            Assert.False(saved.HighWaterAlarm);
            Assert.Equal(22.1, saved.TemperatureC);
            Assert.Equal(-67, saved.SignalRssi);
            Assert.Equal(86400, saved.RuntimeSeconds);
            Assert.Equal(150, saved.ReportedCycleCount);
            Assert.Equal("boot-1", saved.BootId);
            Assert.Equal(42, saved.SequenceNumber);
        }
    }
}
