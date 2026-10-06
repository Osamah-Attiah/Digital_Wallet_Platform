using DigitalWallet.Reporting.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace DigitalWallet.Reporting;

public sealed class ReportJob
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string IdempotencyKey { get; set; } = ""; public string Fingerprint { get; set; } = "";
    public string Type { get; set; } = ""; public string Status { get; set; } = "Queued"; public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public bool IncludesPii { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string CorrelationId { get; set; } = ""; public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public int? RowCount { get; set; }
    public string? FailureCode { get; set; }
    public ReportReceipt Receipt() => new(Id, Type, Status, From, To, IncludesPii, CreatedAt, ExpiresAt, RowCount, FailureCode);
}
public sealed class ReportInbox { public Guid EventId { get; set; } public Guid JobId { get; set; } public DateTimeOffset CompletedAt { get; set; } }
public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> o) : DbContext(o)
{
    public const string Schema = "reporting"; public DbSet<ReportJob> Jobs => Set<ReportJob>(); public DbSet<ReportInbox> Inbox => Set<ReportInbox>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema); b.Entity<ReportJob>(e =>
        {
            e.ToTable("jobs", t => { t.HasCheckConstraint("ck_report_state", "\"Status\" IN ('Queued','Running','Completed','Failed','Expired')"); t.HasCheckConstraint("ck_report_bounds", "\"From\" < \"To\" AND \"To\" - \"From\" <= INTERVAL '31 days' AND \"Attempts\" >= 0"); });
            e.HasKey(x => x.Id); e.Property(x => x.IdempotencyKey).HasMaxLength(128); e.Property(x => x.Fingerprint).HasMaxLength(64); e.Property(x => x.Type).HasMaxLength(40); e.Property(x => x.Status).HasMaxLength(12); e.Property(x => x.CorrelationId).HasMaxLength(128); e.Property(x => x.FailureCode).HasMaxLength(40);
            e.HasIndex(x => new { x.OwnerId, x.IdempotencyKey }).IsUnique(); e.HasIndex(x => new { x.OwnerId, x.CreatedAt, x.Id }); e.HasIndex(x => new { x.Status, x.LeaseUntil });
        });
        b.Entity<ReportInbox>(e => { e.ToTable("inbox"); e.HasKey(x => x.EventId); e.HasIndex(x => x.JobId).IsUnique(); e.HasOne<ReportJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict); });
    }
}
public sealed class ReportingDbContextFactory : IDesignTimeDbContextFactory<ReportingDbContext>
{
    public ReportingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql("Host=localhost;Database=design_only", n => n.MigrationsHistoryTable("__EFMigrationsHistory", ReportingDbContext.Schema)).Options);
}
