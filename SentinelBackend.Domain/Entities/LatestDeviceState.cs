namespace SentinelBackend.Domain.Entities;

public class LatestDeviceState
{
    // PK + FK to Device.Id (int)
    public int DeviceId { get; set; }
    public DateTime LastTelemetryTimestampUtc { get; set; }
    public string? LastMessageId { get; set; }
    public string? LastBootId { get; set; }
    public int? LastSequenceNumber { get; set; }
    public double? PanelVoltage { get; set; }
    public double? PumpCurrent { get; set; }
    public bool? PumpRunning { get; set; }
    public bool? HighWaterAlarm { get; set; }
    public double? TemperatureC { get; set; }
    public int? SignalRssi { get; set; }
    public int? RuntimeSeconds { get; set; }
    public int? ReportedCycleCount { get; set; }
    public long? DerivedCycleCount { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Device Device { get; set; } = default!;
}