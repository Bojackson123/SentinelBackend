namespace SentinelBackend.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;

public class SentinelDbContext : DbContext
{
    public SentinelDbContext(DbContextOptions<SentinelDbContext> options)
        : base(options) { }

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<LatestDeviceState> LatestDeviceStates => Set<LatestDeviceState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.DeviceId).IsUnique();
            e.Property(d => d.DeviceId).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<LatestDeviceState>(e =>
        {
            e.HasKey(s => s.DeviceId);
            e.Property(s => s.DeviceId).HasMaxLength(128).IsRequired();
        });
    }
}