namespace SentinelBackend.Domain.Entities;

public class DesiredPropertyLog
{
    public long Id { get; set; }
    public int DeviceId { get; set; }
    public string PropertyName { get; set; } = default!;
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string RequestedByUserId { get; set; } = default!;
    public DateTime RequestedAt { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public Device Device { get; set; } = default!;
}
