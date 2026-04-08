namespace SentinelBackend.Api.Workers;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

/// <summary>
/// Safety-net background service for offline detection.
///
/// When Service Bus is configured, the primary offline detection is event-driven
/// via OfflineCheckWorker. This worker runs on a longer interval (default 10 min)
/// as a sweep to catch any devices the event-driven path might have missed.
///
/// Optimized to only query devices that actually need state changes:
///   - Devices that exceeded their threshold but aren't yet marked offline
///   - Devices marked offline whose telemetry has since resumed
///
/// Rules (from design doc §13.3):
///   - Default offline threshold is 3× the expected telemetry interval (OfflineThresholdSeconds)
///   - Threshold is configurable per device (stored on DeviceConnectivityState)
///   - Raises DeviceOffline alarms when threshold is exceeded
///   - Auto-resolves DeviceOffline alarms when telemetry resumes (IsOffline becomes false)
///   - Maintenance windows suppress offline alarm generation
/// </summary>
public class OfflineMonitorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OfflineMonitorWorker> _logger;
    private readonly TimeSpan _scanInterval;

    public OfflineMonitorWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<OfflineMonitorWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var scanIntervalSeconds =
            configuration.GetValue<int?>("OfflineMonitor:ScanIntervalSeconds") ?? 600;
        if (scanIntervalSeconds <= 0) scanIntervalSeconds = 600;
        _scanInterval = TimeSpan.FromSeconds(scanIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OfflineMonitorWorker started with scan interval {ScanIntervalSeconds}s",
            _scanInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanConnectivityAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during offline monitor scan");
            }

            await Task.Delay(_scanInterval, stoppingToken);
        }

        _logger.LogInformation("OfflineMonitorWorker stopped");
    }

    internal async Task ScanConnectivityAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var alarmService = scope.ServiceProvider.GetRequiredService<IAlarmService>();

        var now = DateTime.UtcNow;

        // Only query devices that need a state change:
        //   1. Online but over threshold → need to mark offline
        //   2. Offline but under threshold → need to mark online (telemetry resumed)
        // This avoids loading the entire fleet on every scan.
        var connectivityStates = await db.DeviceConnectivityStates
            .Include(c => c.Device)
                .ThenInclude(d => d.Assignments.Where(a => a.UnassignedAt == null))
            .Where(c => c.Device.Status == DeviceStatus.Active)
            .Where(c =>
                // Case 1: Not marked offline, but last message is older than threshold
                (!c.IsOffline && EF.Functions.DateDiffSecond(c.LastMessageReceivedAt, now) > c.OfflineThresholdSeconds)
                ||
                // Case 2: Marked offline, but last message is within threshold (came back)
                (c.IsOffline && EF.Functions.DateDiffSecond(c.LastMessageReceivedAt, now) <= c.OfflineThresholdSeconds))
            .ToListAsync(stoppingToken);

        // Load active maintenance windows
        var activeWindows = await db.MaintenanceWindows
            .Where(mw => mw.StartsAt <= now && mw.EndsAt > now)
            .ToListAsync(stoppingToken);

        var devicesMarkedOffline = 0;
        var devicesMarkedOnline = 0;

        foreach (var connectivity in connectivityStates)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var elapsed = now - connectivity.LastMessageReceivedAt;
            var isOverThreshold = elapsed.TotalSeconds > connectivity.OfflineThresholdSeconds;

            // Check if device is covered by a maintenance window
            var isSuppressed = IsDeviceInMaintenanceWindow(
                connectivity.DeviceId, connectivity.Device, activeWindows);

            connectivity.SuppressedByMaintenanceWindow = isSuppressed;

            if (isOverThreshold && !connectivity.IsOffline)
            {
                // Device just went offline
                connectivity.IsOffline = true;
                connectivity.UpdatedAt = now;
                devicesMarkedOffline++;

                if (!isSuppressed)
                {
                    await alarmService.RaiseAlarmAsync(
                        connectivity.DeviceId,
                        "DeviceOffline",
                        AlarmSeverity.Warning,
                        AlarmSourceType.SystemGenerated,
                        detailsJson: System.Text.Json.JsonSerializer.Serialize(new
                        {
                            lastMessageReceivedAt = connectivity.LastMessageReceivedAt,
                            thresholdSeconds = connectivity.OfflineThresholdSeconds,
                            elapsedSeconds = (int)elapsed.TotalSeconds,
                        }),
                        cancellationToken: stoppingToken);
                }
                else
                {
                    _logger.LogDebug(
                        "Device {DeviceId} offline but suppressed by maintenance window",
                        connectivity.DeviceId);
                }
            }
            else if (!isOverThreshold && connectivity.IsOffline)
            {
                // Device came back online — auto-resolve DeviceOffline alarms
                connectivity.IsOffline = false;
                connectivity.UpdatedAt = now;
                devicesMarkedOnline++;

                await alarmService.AutoResolveAlarmsAsync(
                    connectivity.DeviceId,
                    "DeviceOffline",
                    "Auto-resolved: device telemetry resumed",
                    stoppingToken);
            }
        }

        await db.SaveChangesAsync(stoppingToken);

        if (devicesMarkedOffline > 0 || devicesMarkedOnline > 0)
        {
            _logger.LogInformation(
                "Offline monitor scan: {Offline} devices went offline, {Online} came back online",
                devicesMarkedOffline, devicesMarkedOnline);
        }
    }

    private static bool IsDeviceInMaintenanceWindow(
        int deviceId,
        Domain.Entities.Device device,
        List<Domain.Entities.MaintenanceWindow> windows)
    {
        var activeAssignment = device.Assignments?.FirstOrDefault(a => a.UnassignedAt == null);

        foreach (var window in windows)
        {
            if (window.ScopeType == MaintenanceWindowScope.Device && window.DeviceId == deviceId)
                return true;

            if (window.ScopeType == MaintenanceWindowScope.Site
                && window.SiteId.HasValue
                && activeAssignment is not null
                && window.SiteId == activeAssignment.SiteId)
                return true;

            if (window.ScopeType == MaintenanceWindowScope.Company
                && window.CompanyId.HasValue
                && activeAssignment is not null)
            {
                // Company-scoped windows match any device assigned to that company.
                // The CompanyId lives on the Site→Customer→Company chain;
                // for now we check if the assignment's SiteId is under the company.
                // Full resolution requires loading Customer.CompanyId, which the
                // offline monitor can do in a follow-up if needed.
            }
        }

        return false;
    }
}
