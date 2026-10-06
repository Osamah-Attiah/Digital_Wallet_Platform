using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DigitalWallet.Messaging;

public sealed class OutboxMessage
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public int EventVersion { get; set; }
    public Guid AggregateId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string EnvelopeJson { get; set; } = "";
    public string? TraceParent { get; set; }
    public string? TraceCorrelationId { get; set; }
    public string? TraceCausationId { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
}
public sealed class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : DbContext(options)
{
    public const string Schema = "messaging";
    public DbSet<OutboxMessage> Messages => Set<OutboxMessage>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox", t =>
            {
                t.HasCheckConstraint("ck_outbox_attempts", "\"Attempts\" >= 0 AND \"EventVersion\" > 0");
                t.HasCheckConstraint("ck_outbox_lease", "(\"LeaseToken\" IS NULL) = (\"LeaseUntil\" IS NULL)");
            });
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventType).HasMaxLength(80); e.Property(x => x.LastError).HasMaxLength(256);
            e.Property(x => x.EnvelopeJson).HasColumnType("jsonb");
            e.Property(x => x.TraceParent).HasMaxLength(55);
            e.Property(x => x.TraceCorrelationId).HasMaxLength(128);
            e.Property(x => x.TraceCausationId).HasMaxLength(36);
            e.HasIndex(x => new { x.AggregateId, x.EventType, x.EventVersion }).IsUnique();
            e.HasIndex(x => new { x.NextAttemptAt, x.LeaseUntil }).HasFilter("\"PublishedAt\" IS NULL");
            e.HasIndex(x => x.OccurredAt).HasFilter("\"PublishedAt\" IS NULL");
        });
    }
}
public sealed class OutboxDbContextFactory : IDesignTimeDbContextFactory<OutboxDbContext>
{
    public OutboxDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<OutboxDbContext>()
        .UseNpgsql("Host=localhost;Database=design_only", n => n.MigrationsHistoryTable("__EFMigrationsHistory", OutboxDbContext.Schema)).Options);
}
