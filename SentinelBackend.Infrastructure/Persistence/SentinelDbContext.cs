namespace SentinelBackend.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;

public class SentinelDbContext : DbContext
{
    public SentinelDbContext(DbContextOptions<SentinelDbContext> options)
        : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceAssignment> DeviceAssignments => Set<DeviceAssignment>();
    public DbSet<LatestDeviceState> LatestDeviceStates => Set<LatestDeviceState>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Lead> Leads => Set<Lead>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Company
        modelBuilder.Entity<Company>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(256).IsRequired();
            e.Property(c => c.ContactEmail).HasMaxLength(256).IsRequired();
            e.Property(c => c.BillingEmail).HasMaxLength(256).IsRequired();
            e.Property(c => c.ContactPhone).HasMaxLength(32);
            e.Property(c => c.StripeCustomerId).HasMaxLength(256);
            e.Property(c => c.SubscriptionStatus).HasConversion<string>();
        });

        // Customer
        modelBuilder.Entity<Customer>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.FirstName).HasMaxLength(128).IsRequired();
            e.Property(c => c.LastName).HasMaxLength(128).IsRequired();
            e.Property(c => c.Email).HasMaxLength(256).IsRequired();
            e.Property(c => c.Phone).HasMaxLength(32);
            e.Property(c => c.StripeCustomerId).HasMaxLength(256);
            e.Property(c => c.SubscriptionStatus).HasConversion<string>();

            e.HasOne(c => c.Company)
                .WithMany(co => co.Customers)
                .HasForeignKey(c => c.CompanyId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Site
        modelBuilder.Entity<Site>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).HasMaxLength(256).IsRequired();
            e.Property(s => s.AddressLine1).HasMaxLength(256).IsRequired();
            e.Property(s => s.AddressLine2).HasMaxLength(256);
            e.Property(s => s.City).HasMaxLength(128).IsRequired();
            e.Property(s => s.State).HasMaxLength(128).IsRequired();
            e.Property(s => s.PostalCode).HasMaxLength(32).IsRequired();
            e.Property(s => s.Country).HasMaxLength(128).IsRequired();
            e.Property(s => s.Timezone).HasMaxLength(64).IsRequired();

            e.HasOne(s => s.Customer)
                .WithMany(c => c.Sites)
                .HasForeignKey(s => s.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Device
        modelBuilder.Entity<Device>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.DeviceId).IsUnique();
            e.HasIndex(d => d.SerialNumber).IsUnique();
            e.Property(d => d.DeviceId).HasMaxLength(128);
            e.Property(d => d.SerialNumber).HasMaxLength(64).IsRequired();
            e.Property(d => d.HardwareRevision).HasMaxLength(64);
            e.Property(d => d.FirmwareVersion).HasMaxLength(64);
            e.Property(d => d.Status).HasConversion<string>();
        });

        // LatestDeviceState
        // Dependent side of the Device one-to-one; DeviceId is an int FK to Device.Id
        modelBuilder.Entity<LatestDeviceState>(e =>
        {
            e.HasKey(s => s.DeviceId);

            e.HasOne(s => s.Device)
                .WithOne(d => d.LatestState)
                .HasForeignKey<LatestDeviceState>(s => s.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DeviceAssignment
        modelBuilder.Entity<DeviceAssignment>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.AssignedByUserId).HasMaxLength(450).IsRequired();
            e.Property(a => a.UnassignedByUserId).HasMaxLength(450);
            e.Property(a => a.UnassignmentReason).HasConversion<string>();

            e.HasOne(a => a.Device)
                .WithMany(d => d.Assignments)
                .HasForeignKey(a => a.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(a => a.Site)
                .WithMany(s => s.DeviceAssignments)
                .HasForeignKey(a => a.SiteId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Subscription
        modelBuilder.Entity<Subscription>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.StripeSubscriptionId).HasMaxLength(256).IsRequired();
            e.Property(s => s.StripeCustomerId).HasMaxLength(256).IsRequired();
            e.Property(s => s.OwnerType).HasConversion<string>();
            e.Property(s => s.Status).HasConversion<string>();

            e.HasOne(s => s.Company)
                .WithOne(c => c.Subscription)
                .HasForeignKey<Subscription>(s => s.CompanyId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(s => s.Customer)
                .WithOne(c => c.Subscription)
                .HasForeignKey<Subscription>(s => s.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Lead
        modelBuilder.Entity<Lead>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Status).HasConversion<string>();
            e.Property(l => l.Notes).HasMaxLength(2000);

            e.HasOne(l => l.Device)
                .WithOne(d => d.Lead)
                .HasForeignKey<Lead>(l => l.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(l => l.Site)
                .WithMany()
                .HasForeignKey(l => l.SiteId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(l => l.PreviousCompany)
                .WithMany()
                .HasForeignKey(l => l.PreviousCompanyId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(l => l.PreviousCustomer)
                .WithMany()
                .HasForeignKey(l => l.PreviousCustomerId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}