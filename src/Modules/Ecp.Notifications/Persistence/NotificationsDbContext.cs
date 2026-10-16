using Ecp.Notifications.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Notifications.Persistence;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationDelivery> Deliveries => Set<NotificationDelivery>();
    public DbSet<DeliveryAttempt> Attempts => Set<DeliveryAttempt>();
    public DbSet<ReminderWindow> ReminderWindows => Set<ReminderWindow>();
    public DbSet<ReminderTracking> Tracking => Set<ReminderTracking>();
    public DbSet<CapturedEmail> Captured => Set<CapturedEmail>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<DispatchCursor> Cursors => Set<DispatchCursor>();
    protected override string SchemaName => NotificationsModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<Notification>(e =>
        {
            e.ToTable("Notification");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.SubjectKind).HasMaxLength(20);
            e.Property(x => x.Classification).HasMaxLength(30).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            e.Property(x => x.LinkPath).HasMaxLength(300);
            e.Property(x => x.DedupeKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ReminderKind).HasMaxLength(30);
            e.Property(x => x.AcknowledgedBy).HasMaxLength(200);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.DedupeKey }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.RecipientUserId, x.CreatedUtc });
            e.HasIndex(x => new { x.TenantId, x.ContractId, x.CreatedUtc });
        });

        mb.Entity<NotificationDelivery>(e =>
        {
            e.ToTable("NotificationDelivery");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.Status, x.NextAttemptUtc });
            e.HasIndex(x => new { x.TenantId, x.NotificationId, x.Channel }).IsUnique();
            e.HasOne<Notification>().WithMany().HasForeignKey(x => new { x.TenantId, x.NotificationId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("DeliveryAttempt");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.Error).HasMaxLength(500);
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.DeliveryId, x.Number }).IsUnique();
            e.HasOne<NotificationDelivery>().WithMany().HasForeignKey(x => new { x.TenantId, x.DeliveryId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<ReminderWindow>(e =>
        {
            e.ToTable("ReminderWindow");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(30).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.ContractId, x.Kind, x.WindowDays, x.DueDate }).IsUnique();
        });

        mb.Entity<ReminderTracking>(e =>
        {
            e.ToTable("ReminderTracking");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Classification).HasMaxLength(30).IsRequired();
            e.Property(x => x.LastReminderKind).HasMaxLength(30).IsRequired();
            e.Property(x => x.AcknowledgedBy).HasMaxLength(200);
            e.Ignore(x => x.IsAcknowledged);
            e.HasIndex(x => new { x.TenantId, x.ContractId }).IsUnique();
        });

        mb.Entity<CapturedEmail>(e =>
        {
            e.ToTable("CapturedEmail");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ToAddress).HasMaxLength(320).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            e.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.CapturedUtc });
        });

        mb.Entity<JobRun>(e =>
        {
            e.ToTable("JobRun");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.JobName).HasMaxLength(60).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.JobName, x.StartedUtc });
        });

        mb.Entity<DispatchCursor>(e =>
        {
            e.ToTable("DispatchCursor");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Source).HasMaxLength(40).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Source }).IsUnique();
        });
    }
}
