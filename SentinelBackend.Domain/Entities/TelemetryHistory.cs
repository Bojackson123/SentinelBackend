namespace SentinelBackend.Domain.Entities;

public class TelemetryHistory
{
    public long Id { get; set; }
    public int DeviceId { get; set; }
    public string MessageId { get; set; } = default!;
    public string MessageType { get; set; } = default!;
    public DateTime TimestampUtc { get; set; }
    public DateTime EnqueuedAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }

    // Ownership snapshot at ingest time
    public int? DeviceAssignmentId { get; set; }
    public int? SiteId { get; set; }
    public int? CustomerId { get; set; }
    public int? CompanyId { get; set; }

    // Telemetry fields
    public double? PanelVoltage { get; set; }
    public double? PumpCurrent { get; set; }
    public bool? PumpRunning { get; set; }
    public bool? HighWaterAlarm { get; set; }
    public double? TemperatureC { get; set; }
    public int? SignalRssi { get; set; }
    public int? RuntimeSeconds { get; set; }
    public int? ReportedCycleCount { get; set; }
    public long? DerivedCycleCount { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? BootId { get; set; }
    public int? SequenceNumber { get; set; }
    public string? RawPayloadBlobUri { get; set; }

    public Device Device { get; set; } = default!;
}
