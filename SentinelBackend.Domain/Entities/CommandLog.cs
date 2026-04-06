namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

public class CommandLog
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string CommandType { get; set; } = default!;
    public CommandStatus Status { get; set; }
    public string RequestedByUserId { get; set; } = default!;
    public DateTime RequestedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResponseJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Device Device { get; set; } = default!;
}
