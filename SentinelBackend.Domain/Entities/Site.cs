namespace SentinelBackend.Domain.Entities;

public class Site
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string Name { get; set; } = default!;
    public string AddressLine1 { get; set; } = default!;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = default!;
    public string State { get; set; } = default!;
    public string PostalCode { get; set; } = default!;
    public string Country { get; set; } = default!;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string Timezone { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Customer Customer { get; set; } = default!;
    public ICollection<DeviceAssignment> DeviceAssignments { get; set; } = [];
}