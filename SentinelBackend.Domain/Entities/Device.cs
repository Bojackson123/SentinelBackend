namespace SentinelBackend.Domain.Entities;

public class Device
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = default!;
    public string? Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public LatestDeviceState? LatestState { get; set; }
}