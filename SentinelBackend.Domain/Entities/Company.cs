namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

public class Company
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string ContactEmail { get; set; } = default!;
    public string? ContactPhone { get; set; }
    public string BillingEmail { get; set; } = default!;
    public string? StripeCustomerId { get; set; }
    public SubscriptionStatus SubscriptionStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Customer> Customers { get; set; } = [];
    public Subscription? Subscription { get; set; }
}