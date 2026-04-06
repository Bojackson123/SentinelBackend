namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

public class MaintenanceWindow
{
    public int Id { get; set; }
    public MaintenanceWindowScope ScopeType { get; set; }
    public int? DeviceId { get; set; }
    public int? SiteId { get; set; }
    public int? CompanyId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? Reason { get; set; }
    public string CreatedByUserId { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}
