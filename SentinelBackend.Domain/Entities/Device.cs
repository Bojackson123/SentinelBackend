namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

public class Device
{
    public int Id { get; set; }

    // IoT Hub identity — set on first boot via DPS
    public string? DeviceId { get; set; }

    // Set at manufacturing, exists before first boot
    public string SerialNumber { get; set; } = default!;

    public string? HardwareRevision { get; set; }
    public string? FirmwareVersion { get; set; }
    public DeviceStatus Status { get; set; }
    public DateTime? ProvisionedAt { get; set; }
    public DateTime? ManufacturedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public LatestDeviceState? LatestState { get; set; }
    public DeviceConnectivityState? ConnectivityState { get; set; }
    public ICollection<DeviceAssignment> Assignments { get; set; } = [];
    public ICollection<TelemetryHistory> TelemetryHistory { get; set; } = [];
    public ICollection<Alarm> Alarms { get; set; } = [];
    public ICollection<CommandLog> Commands { get; set; } = [];
    public Lead? Lead { get; set; }
}