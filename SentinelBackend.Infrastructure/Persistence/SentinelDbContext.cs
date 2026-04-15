namespace SentinelBackend.Infrastructure.Persistence;

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;

public class SentinelDbContext : IdentityDbContext<ApplicationUser>
{
    public SentinelDbContext(DbContextOptions<SentinelDbContext> options)
        : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceAssignment> DeviceAssignments => Set<DeviceAssignment>();
    public DbSet<LatestDeviceState> LatestDeviceStates => Set<LatestDeviceState>();
    public DbSet<DeviceConnectivityState> DeviceConnectivityStates => Set<DeviceConnectivityState>();
    public DbSet<TelemetryHistory> TelemetryHistory => Set<TelemetryHistory>();
    public DbSet<Alarm> Alarms => Set<Alarm>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();
    public DbSet<FailedIngressMessage> FailedIngressMessages => Set<FailedIngressMessage>();
    public DbSet<CommandLog> CommandLogs => Set<CommandLog>();
    public DbSet<DesiredPropertyLog> DesiredPropertyLogs => Set<DesiredPropertyLog>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<NotificationIncident> NotificationIncidents => Set<NotificationIncident>();
    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();
    public DbSet<EscalationEvent> EscalationEvents => Set<EscalationEvent>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Lead> Leads => Set<Lead>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Company ──────────────────────────────────────────────
        modelBuilder.Entity<Company>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(256).IsRequired();
            e.Property(c => c.ContactEmail).HasMaxLength(256).IsRequired();
            e.Property(c => c.BillingEmail).HasMaxLength(256).IsRequired();
            e.Property(c => c.ContactPhone).HasMaxLength(32);
            e.Property(c => c.StripeCustomerId).HasMaxLength(256);
            e.Property(c => c.SubscriptionStatus).HasConversion<string>();
            e.Property(c => c.IsInternal).HasDefaultValue(false);
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        // ── Customer ─────────────────────────────────────────────
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

        // ── Site ─────────────────────────────────────────────────
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
            e.HasQueryFilter(s => !s.IsDeleted);

            e.HasOne(s => s.Customer)
                .WithMany(c => c.Sites)
                .HasForeignKey(s => s.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Device ───────────────────────────────────────────────
        modelBuilder.Entity<Device>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.DeviceId).IsUnique().HasFilter("[DeviceId] IS NOT NULL");
            e.HasIndex(d => d.SerialNumber).IsUnique();
            e.Property(d => d.DeviceId).HasMaxLength(128);
            e.Property(d => d.SerialNumber).HasMaxLength(64).IsRequired();
            e.Property(d => d.HardwareRevision).HasMaxLength(64);
            e.Property(d => d.FirmwareVersion).HasMaxLength(64);
            e.Property(d => d.Status).HasConversion<string>();
            e.HasQueryFilter(d => !d.IsDeleted);
        });

        // ── LatestDeviceState ────────────────────────────────────
        modelBuilder.Entity<LatestDeviceState>(e =>
        {
            e.HasKey(s => s.DeviceId);
            e.Property(s => s.LastMessageId).HasMaxLength(256);
            e.Property(s => s.LastBootId).HasMaxLength(256);
            e.HasQueryFilter(s => !s.Device.IsDeleted);

            e.HasOne(s => s.Device)
                .WithOne(d => d.LatestState)
                .HasForeignKey<LatestDeviceState>(s => s.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── DeviceConnectivityState ──────────────────────────────
        modelBuilder.Entity<DeviceConnectivityState>(e =>
        {
            e.HasKey(c => c.DeviceId);
            e.Property(c => c.LastMessageType).HasMaxLength(64);
            e.Property(c => c.LastBootId).HasMaxLength(256);
            e.HasQueryFilter(c => !c.Device.IsDeleted);

            e.HasOne(c => c.Device)
                .WithOne(d => d.ConnectivityState)
                .HasForeignKey<DeviceConnectivityState>(c => c.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── DeviceAssignment ─────────────────────────────────────
        modelBuilder.Entity<DeviceAssignment>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.AssignedByUserId).HasMaxLength(450).IsRequired();
            e.Property(a => a.UnassignedByUserId).HasMaxLength(450);
            e.Property(a => a.UnassignmentReason).HasConversion<string>();
            e.HasQueryFilter(a => !a.Device.IsDeleted);

            e.HasOne(a => a.Device)
                .WithMany(d => d.Assignments)
                .HasForeignKey(a => a.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(a => a.Site)
                .WithMany(s => s.DeviceAssignments)
                .HasForeignKey(a => a.SiteId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── TelemetryHistory ─────────────────────────────────────
        modelBuilder.Entity<TelemetryHistory>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.MessageId).HasMaxLength(256).IsRequired();
            e.Property(t => t.MessageType).HasMaxLength(64).IsRequired();
            e.Property(t => t.FirmwareVersion).HasMaxLength(64);
            e.Property(t => t.BootId).HasMaxLength(256);
            e.Property(t => t.RawPayloadBlobUri).HasMaxLength(1024);
            e.HasQueryFilter(t => !t.Device.IsDeleted);

            e.HasIndex(t => new { t.DeviceId, t.MessageId }).IsUnique();
            e.HasIndex(t => new { t.DeviceId, t.TimestampUtc })
                .IsDescending(false, true);

            e.HasOne(t => t.Device)
                .WithMany(d => d.TelemetryHistory)
                .HasForeignKey(t => t.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Alarm ────────────────────────────────────────────────
        modelBuilder.Entity<Alarm>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.AlarmType).HasMaxLength(128).IsRequired();
            e.Property(a => a.Severity).HasConversion<string>();
            e.Property(a => a.Status).HasConversion<string>();
            e.Property(a => a.SourceType).HasConversion<string>();
            e.Property(a => a.TriggerMessageId).HasMaxLength(256);
            e.Property(a => a.SuppressReason).HasMaxLength(1000);
            e.Property(a => a.SuppressedByUserId).HasMaxLength(450);
            e.HasQueryFilter(a => !a.Device.IsDeleted);

            e.HasIndex(a => new { a.DeviceId, a.AlarmType, a.Status });

            e.HasOne(a => a.Device)
                .WithMany(d => d.Alarms)
                .HasForeignKey(a => a.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── AlarmEvent ───────────────────────────────────────────
        modelBuilder.Entity<AlarmEvent>(e =>
        {
            e.HasKey(ae => ae.Id);
            e.Property(ae => ae.EventType).HasMaxLength(64).IsRequired();
            e.Property(ae => ae.UserId).HasMaxLength(450);
            e.Property(ae => ae.Reason).HasMaxLength(1000);
            e.HasQueryFilter(ae => !ae.Alarm.Device.IsDeleted);

            e.HasOne(ae => ae.Alarm)
                .WithMany(a => a.Events)
                .HasForeignKey(ae => ae.AlarmId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── FailedIngressMessage ─────────────────────────────────
        modelBuilder.Entity<FailedIngressMessage>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.SourceDeviceId).HasMaxLength(128);
            e.Property(f => f.MessageId).HasMaxLength(256);
            e.Property(f => f.PartitionId).HasMaxLength(64).IsRequired();
            e.Property(f => f.Offset).HasMaxLength(64).IsRequired();
            e.Property(f => f.FailureReason).HasMaxLength(256).IsRequired();
            e.Property(f => f.ErrorMessage).HasMaxLength(2000);
        });

        // ── CommandLog ───────────────────────────────────────────
        modelBuilder.Entity<CommandLog>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.CommandType).HasMaxLength(64).IsRequired();
            e.Property(c => c.Status).HasConversion<string>();
            e.Property(c => c.RequestedByUserId).HasMaxLength(450).IsRequired();
            e.Property(c => c.ErrorMessage).HasMaxLength(2000);
            e.HasQueryFilter(c => !c.Device.IsDeleted);

            e.HasIndex(c => new { c.DeviceId, c.Status });

            e.HasOne(c => c.Device)
                .WithMany(d => d.Commands)
                .HasForeignKey(c => c.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── DesiredPropertyLog ────────────────────────────────────
        modelBuilder.Entity<DesiredPropertyLog>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.PropertyName).HasMaxLength(256).IsRequired();
            e.Property(p => p.PreviousValue).HasMaxLength(1000);
            e.Property(p => p.NewValue).HasMaxLength(1000);
            e.Property(p => p.RequestedByUserId).HasMaxLength(450).IsRequired();
            e.Property(p => p.ErrorMessage).HasMaxLength(2000);
            e.HasQueryFilter(p => !p.Device.IsDeleted);

            e.HasIndex(p => new { p.DeviceId, p.RequestedAt });

            e.HasOne(p => p.Device)
                .WithMany()
                .HasForeignKey(p => p.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── NotificationIncident ─────────────────────────────────
        modelBuilder.Entity<NotificationIncident>(e =>
        {
            e.HasKey(n => n.Id);
            e.Property(n => n.Status).HasConversion<string>();
            e.Property(n => n.AcknowledgedByUserId).HasMaxLength(450);
            e.HasQueryFilter(n => !n.Device.IsDeleted);

            e.HasIndex(n => new { n.AlarmId });
            e.HasIndex(n => new { n.DeviceId, n.Status });

            e.HasOne(n => n.Alarm)
                .WithMany(a => a.NotificationIncidents)
                .HasForeignKey(n => n.AlarmId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(n => n.Device)
                .WithMany()
                .HasForeignKey(n => n.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── NotificationAttempt ──────────────────────────────────
        modelBuilder.Entity<NotificationAttempt>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Channel).HasConversion<string>();
            e.Property(a => a.Status).HasConversion<string>();
            e.Property(a => a.Recipient).HasMaxLength(512).IsRequired();
            e.Property(a => a.ProviderMessageId).HasMaxLength(256);
            e.Property(a => a.ErrorMessage).HasMaxLength(2000);

            e.HasIndex(a => new { a.NotificationIncidentId, a.Status });
            e.HasIndex(a => new { a.Status, a.ScheduledAt });

            e.HasOne(a => a.NotificationIncident)
                .WithMany(n => n.Attempts)
                .HasForeignKey(a => a.NotificationIncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── EscalationEvent ──────────────────────────────────────
        modelBuilder.Entity<EscalationEvent>(e =>
        {
            e.HasKey(ev => ev.Id);
            e.Property(ev => ev.Reason).HasMaxLength(1000).IsRequired();

            e.HasOne(ev => ev.NotificationIncident)
                .WithMany(n => n.Escalations)
                .HasForeignKey(ev => ev.NotificationIncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── MaintenanceWindow ────────────────────────────────────
        modelBuilder.Entity<MaintenanceWindow>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.ScopeType).HasConversion<string>();
            e.Property(m => m.Reason).HasMaxLength(1000);
            e.Property(m => m.CreatedByUserId).HasMaxLength(450).IsRequired();

            e.HasIndex(m => new { m.ScopeType, m.EndsAt });
        });

        // ── Subscription ─────────────────────────────────────────
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

        // ── Lead ─────────────────────────────────────────────────
        modelBuilder.Entity<Lead>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Status).HasConversion<string>();
            e.Property(l => l.Notes).HasMaxLength(2000);
            e.HasQueryFilter(l => !l.Device.IsDeleted);

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

        // ── ApplicationUser ──────────────────────────────────────
        modelBuilder.Entity<ApplicationUser>(e =>
        {
            e.HasOne(u => u.Company)
                .WithMany()
                .HasForeignKey(u => u.CompanyId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(u => u.Customer)
                .WithMany()
                .HasForeignKey(u => u.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}