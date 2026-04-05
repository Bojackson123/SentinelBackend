namespace SentinelBackend.Contracts;

public class TelemetryMessage
{
    public string? DeviceId { get; set; }
    public DateTime? TimestampUtc { get; set; }
    public double? PanelVoltage { get; set; }
    public double? PumpCurrent { get; set; }
    public int? CycleCount { get; set; }
    public int? RuntimeSeconds { get; set; }
    public bool? HighWaterAlarm { get; set; }
    public double? TemperatureC { get; set; }
    public int? SignalRssi { get; set; }
    public string? FirmwareVersion { get; set; }
}