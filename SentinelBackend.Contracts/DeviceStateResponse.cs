namespace SentinelBackend.Contracts;

public class DeviceStateResponse
{
    public string DeviceId { get; set; } = default!;
    public DateTime LastSeenAt { get; set; }
    public double? PanelVoltage { get; set; }
    public double? PumpCurrent { get; set; }
    public bool? HighWaterAlarm { get; set; }
    public double? TemperatureC { get; set; }
    public int? SignalRssi { get; set; }
    public int? RuntimeSeconds { get; set; }
    public int? CycleCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}