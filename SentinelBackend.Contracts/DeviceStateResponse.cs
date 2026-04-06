namespace SentinelBackend.Contracts;

public class DeviceStateResponse
{
    public string DeviceId { get; set; } = default!;
    public DateTime LastTelemetryTimestampUtc { get; set; }
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
}