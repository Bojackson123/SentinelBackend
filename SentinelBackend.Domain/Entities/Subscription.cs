namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

public class Subscription
{
    public int Id { get; set; }
    public string StripeSubscriptionId { get; set; } = default!;
    public string StripeCustomerId { get; set; } = default!;
    public SubscriptionOwnerType OwnerType { get; set; }
    public int? CompanyId { get; set; }
    public int? CustomerId { get; set; }
    public SubscriptionStatus Status { get; set; }
    public DateTime CurrentPeriodStart { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Company? Company { get; set; }
    public Customer? Customer { get; set; }
}