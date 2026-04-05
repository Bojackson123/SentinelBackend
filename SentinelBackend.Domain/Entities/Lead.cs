namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

public class Lead
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public int SiteId { get; set; }
    public int? PreviousCompanyId { get; set; }
    public int? PreviousCustomerId { get; set; }
    public LeadStatus Status { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Device Device { get; set; } = default!;
    public Site Site { get; set; } = default!;
    public Company? PreviousCompany { get; set; }
    public Customer? PreviousCustomer { get; set; }
}