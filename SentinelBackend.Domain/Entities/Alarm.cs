namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

public class Alarm
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public int? DeviceAssignmentId { get; set; }
    public int? SiteId { get; set; }
    public int? CustomerId { get; set; }
    public int? CompanyId { get; set; }
    public string AlarmType { get; set; } = default!;
    public AlarmSeverity Severity { get; set; }
    public AlarmStatus Status { get; set; }
    public AlarmSourceType SourceType { get; set; }
    public string? TriggerMessageId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? SuppressReason { get; set; }
    public string? SuppressedByUserId { get; set; }
    public string? DetailsJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Device Device { get; set; } = default!;
    public ICollection<AlarmEvent> Events { get; set; } = [];
}
