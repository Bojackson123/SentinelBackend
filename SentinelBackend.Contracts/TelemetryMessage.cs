namespace SentinelBackend.Contracts;

public class TelemetryMessage
{
    // Envelope — required per design doc
    public string? MessageId { get; set; }
    public DateTime? TimestampUtc { get; set; }
    public int? SchemaVersion { get; set; }

    // Optional envelope
    public string? BootId { get; set; }
    public int? SequenceNumber { get; set; }

    // Telemetry fields
    public double? PanelVoltage { get; set; }
    public double? PumpCurrent { get; set; }
    public bool? PumpRunning { get; set; }
    public int? ReportedCycleCount { get; set; }
    public int? RuntimeSeconds { get; set; }
    public bool? HighWaterAlarm { get; set; }
    public double? TemperatureC { get; set; }
    public int? SignalRssi { get; set; }
    public string? FirmwareVersion { get; set; }
}