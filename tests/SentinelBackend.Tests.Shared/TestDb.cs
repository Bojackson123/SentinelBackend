namespace SentinelBackend.Tests.Shared;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

/// <summary>
/// Shared test fixture helpers: InMemory EF context factory and canonical seed data.
/// </summary>
public static class TestDb
{
    public static SentinelDbContext Create() =>
        new(new DbContextOptionsBuilder<SentinelDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Seeds a company → customer → site → device hierarchy and returns all created entities.</summary>
    public static async Task<SeedData> SeedFullHierarchyAsync(SentinelDbContext db)
    {
        var company = new Company
        {
            Name = "Acme Pumps",
            ContactEmail = "admin@acme.com",
            BillingEmail = "billing@acme.com",
            SubscriptionStatus = SubscriptionStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var customer = new Customer
        {
            CompanyId = company.Id,
            FirstName = "Jane",
            LastName = "Doe",
            Email = "jane@example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var site = new Site
        {
            CustomerId = customer.Id,
            Name = "Jane's Residence",
            AddressLine1 = "456 Oak Ave",
            City = "Austin",
            State = "TX",
            PostalCode = "73301",
            Country = "US",
            Timezone = "America/Chicago",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Sites.Add(site);

        var device = new Device
        {
            SerialNumber = "GP-202604-00001",
            DeviceId = "GP-202604-00001",
            Status = DeviceStatus.Unprovisioned,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return new SeedData(company, customer, site, device);
    }

    /// <summary>Seed a second company hierarchy for cross-tenant isolation tests.</summary>
    public static async Task<SeedData> SeedSecondHierarchyAsync(SentinelDbContext db)
    {
        var company = new Company
        {
            Name = "Rival Pumps",
            ContactEmail = "admin@rival.com",
            BillingEmail = "billing@rival.com",
            SubscriptionStatus = SubscriptionStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var customer = new Customer
        {
            CompanyId = company.Id,
            FirstName = "Bob",
            LastName = "Smith",
            Email = "bob@rival.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var site = new Site
        {
            CustomerId = customer.Id,
            Name = "Bob's House",
            AddressLine1 = "789 Elm St",
            City = "Dallas",
            State = "TX",
            PostalCode = "75001",
            Country = "US",
            Timezone = "America/Chicago",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Sites.Add(site);

        var device = new Device
        {
            SerialNumber = "GP-202604-00099",
            DeviceId = "GP-202604-00099",
            Status = DeviceStatus.Unprovisioned,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return new SeedData(company, customer, site, device);
    }
}

public record SeedData(Company Company, Customer Customer, Site Site, Device Device);
