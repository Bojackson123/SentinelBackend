namespace SentinelBackend.Domain.Entities;

public class FailedIngressMessage
{
    public long Id { get; set; }
    public string? SourceDeviceId { get; set; }
    public string? MessageId { get; set; }
    public string PartitionId { get; set; } = default!;
    public string Offset { get; set; } = default!;
    public DateTime EnqueuedAt { get; set; }
    public string FailureReason { get; set; } = default!;
    public string? ErrorMessage { get; set; }
    public string? RawPayload { get; set; }
    public string? HeadersJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
