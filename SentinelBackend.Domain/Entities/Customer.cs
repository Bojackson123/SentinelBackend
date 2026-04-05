namespace SentinelBackend.Domain.Entities;

using SentinelBackend.Domain.Enums;

public class Customer
{
    public int Id { get; set; }
    public int? CompanyId { get; set; }
    public string FirstName { get; set; } = default!;
    public string LastName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Phone { get; set; }
    public string? StripeCustomerId { get; set; }

    // Only relevant for standalone homeowners
    public SubscriptionStatus? SubscriptionStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Company? Company { get; set; }
    public ICollection<Site> Sites { get; set; } = [];
    public Subscription? Subscription { get; set; }
}