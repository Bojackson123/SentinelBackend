namespace SentinelBackend.Domain.Entities;

public class DeviceConnectivityState
{
    public int DeviceId { get; set; }
    public DateTime LastMessageReceivedAt { get; set; }
    public DateTime? LastTelemetryReceivedAt { get; set; }
    public DateTime? LastEnqueuedAtUtc { get; set; }
    public string? LastMessageType { get; set; }
    public string? LastBootId { get; set; }
    public int OfflineThresholdSeconds { get; set; } = 900; // 3x 5-minute interval
    public bool IsOffline { get; set; }
    public bool SuppressedByMaintenanceWindow { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Device Device { get; set; } = default!;
}
