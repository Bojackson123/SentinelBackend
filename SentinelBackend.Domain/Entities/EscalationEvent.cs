namespace SentinelBackend.Domain.Entities;

/// <summary>
/// Records an escalation step within a notification incident.
/// Created when a notification is escalated to a higher level after
/// failed delivery or lack of acknowledgment.
/// </summary>
public class EscalationEvent
{
    public int Id { get; set; }
    public int NotificationIncidentId { get; set; }
    public int FromLevel { get; set; }
    public int ToLevel { get; set; }
    public string Reason { get; set; } = default!;
    public DateTime CreatedAt { get; set; }

    public NotificationIncident NotificationIncident { get; set; } = default!;
}
