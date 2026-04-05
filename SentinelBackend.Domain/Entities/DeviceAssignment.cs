namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

public class DeviceAssignment
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public int SiteId { get; set; }

    // String because ASP.NET Core Identity uses string PKs
    public string AssignedByUserId { get; set; } = default!;
    public DateTime AssignedAt { get; set; }

    public string? UnassignedByUserId { get; set; }
    public DateTime? UnassignedAt { get; set; }
    public UnassignmentReason? UnassignmentReason { get; set; }

    public Device Device { get; set; } = default!;
    public Site Site { get; set; } = default!;
}