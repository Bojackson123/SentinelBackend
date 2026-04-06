namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

/// <summary>
/// Tracks a single delivery attempt for a notification incident.
/// Each attempt targets a specific channel and recipient.
/// </summary>
public class NotificationAttempt
{
    public long Id { get; set; }
    public int NotificationIncidentId { get; set; }
    public NotificationChannel Channel { get; set; }
    public NotificationStatus Status { get; set; }
    public string Recipient { get; set; } = default!;
    public int AttemptNumber { get; set; }
    public int EscalationLevel { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public NotificationIncident NotificationIncident { get; set; } = default!;
}
