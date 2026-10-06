using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DigitalWallet.Notifications;

public sealed class ConsumerInbox
{
    public string ConsumerName { get; set; } = "";
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
public sealed class InAppNotification
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid EventId { get; set; }
    public Guid AggregateId { get; set; }
    public string Kind { get; set; } = "";
    public string Direction { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "SAR";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
public sealed class NotificationPreference
{
    public Guid CustomerId { get; set; }
    public bool InAppEnabled { get; set; } = true;
    public bool TransfersEnabled { get; set; } = true;
    public bool ReversalsEnabled { get; set; } = true;
}
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";
    public DbSet<ConsumerInbox> Inbox => Set<ConsumerInbox>();
    public DbSet<InAppNotification> Notifications => Set<InAppNotification>();
    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<ConsumerInbox>(e => { e.ToTable("inbox"); e.HasKey(x => new { x.ConsumerName, x.EventId }); e.Property(x => x.ConsumerName).HasMaxLength(80); });
        b.Entity<NotificationPreference>(e => { e.ToTable("preferences"); e.HasKey(x => x.CustomerId); });
        b.Entity<InAppNotification>(e =>
        {
            e.ToTable("notifications", t => t.HasCheckConstraint("ck_notification_amount", "\"AmountMinor\" > 0 AND \"Currency\" = 'SAR'"));
            e.HasKey(x => x.Id); e.Property(x => x.Kind).HasMaxLength(80); e.Property(x => x.Direction).HasMaxLength(20); e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => new { x.CustomerId, x.EventId }).IsUnique();
            e.HasIndex(x => new { x.CustomerId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
        });
    }
}
public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<NotificationsDbContext>()
        .UseNpgsql("Host=localhost;Database=design_only", n => n.MigrationsHistoryTable("__EFMigrationsHistory", NotificationsDbContext.Schema)).Options);
}
