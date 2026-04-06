namespace SentinelBackend.Domain.Entities;

public class AlarmEvent
{
    public int Id { get; set; }
    public int AlarmId { get; set; }
    public string EventType { get; set; } = default!;
    public string? UserId { get; set; }
    public string? Reason { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }

    public Alarm Alarm { get; set; } = default!;
}
