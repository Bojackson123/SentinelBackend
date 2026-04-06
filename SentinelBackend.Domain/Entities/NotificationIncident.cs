namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

/// <summary>
/// Tracks a notification workflow triggered by an alarm.
/// One NotificationIncident per alarm; contains one or more NotificationAttempts.
/// </summary>
public class NotificationIncident
{
    public int Id { get; set; }
    public int AlarmId { get; set; }
    public int DeviceId { get; set; }
    public int? SiteId { get; set; }
    public int? CustomerId { get; set; }
    public int? CompanyId { get; set; }
    public NotificationIncidentStatus Status { get; set; }
    public int CurrentEscalationLevel { get; set; }
    public int MaxAttempts { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Alarm Alarm { get; set; } = default!;
    public Device Device { get; set; } = default!;
    public ICollection<NotificationAttempt> Attempts { get; set; } = [];
    public ICollection<EscalationEvent> Escalations { get; set; } = [];
}
