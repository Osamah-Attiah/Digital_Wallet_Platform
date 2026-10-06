using System.Data.Common;
using System.Text.Json;
using DigitalWallet.Audit.Contracts;
using DigitalWallet.SharedKernel.Administration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace DigitalWallet.Audit;

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid ActorId { get; set; }
    public string ActorType { get; set; } = "Staff";
    public string Action { get; set; } = ""; public string ResourceType { get; set; } = ""; public string ResourceId { get; set; } = "";
    public string Result { get; set; } = ""; public DateTimeOffset OccurredAt { get; set; }
    public string CorrelationId { get; set; } = ""; public string? Reason { get; set; }
    public string? Changes { get; set; }
}
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public const string Schema = "audit"; public DbSet<AuditLog> Logs => Set<AuditLog>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema); b.Entity<AuditLog>(e =>
        {
            e.ToTable("logs", t => t.HasCheckConstraint("ck_audit_result", "\"Result\" IN ('Success','Denied','Failed')")); e.HasKey(x => x.Id);
            e.Property(x => x.ActorType).HasMaxLength(20); e.Property(x => x.Action).HasMaxLength(100); e.Property(x => x.ResourceType).HasMaxLength(40);
            e.Property(x => x.ResourceId).HasMaxLength(128); e.Property(x => x.Result).HasMaxLength(10); e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.Property(x => x.Reason).HasMaxLength(40); e.Property(x => x.Changes).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OccurredAt, x.Id }); e.HasIndex(x => new { x.ActorId, x.OccurredAt, x.Id }); e.HasIndex(x => new { x.ResourceType, x.ResourceId, x.OccurredAt }); e.HasIndex(x => new { x.Action, x.OccurredAt });
        });
    }
    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        if (ChangeTracker.Entries<AuditLog>().Any(e => e.State is EntityState.Modified or EntityState.Deleted)) throw new InvalidOperationException("Audit is append-only.");
        return base.SaveChangesAsync(ct);
    }
}
public sealed class AuditWriter(AuditDbContext db, TimeProvider clock) : IAuditWriter
{
    public async Task AppendAsync(AuditFact f, CancellationToken ct)
    {
        if (f.ActorType is not ("Staff" or "Worker" or "Anonymous") || f.Result is not ("Success" or "Denied" or "Failed") ||
            f.Action.Length > 100 || f.ResourceType.Length > 40 || f.ResourceId.Length > 128 || f.CorrelationId.Length > 128) throw new AdminException("AUDIT_FACT_INVALID");
        // Free text, request bodies and arbitrary dictionaries are never persisted.
        if (f.Reason is not (null or "AdministrativeReview" or "ComplianceReview" or "CustomerRequest" or "PermissionRevoked")) throw new AdminException("AUDIT_FACT_INVALID");
        if (f.Changes != null)
        {
            using var json = JsonDocument.Parse(f.Changes);
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Any(p => p.Name is not ("status" or "kycStatus" or "version" or "type" or "rowCount" or "pii") ||
                p.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))) throw new AdminException("AUDIT_FACT_INVALID");
        }
        var at = clock.GetUtcNow(); at = at.AddTicks(-(at.Ticks % 10));
        db.Logs.Add(new() { ActorId = f.ActorId, ActorType = f.ActorType, Action = f.Action, ResourceType = f.ResourceType, ResourceId = f.ResourceId, Result = f.Result, OccurredAt = at, CorrelationId = f.CorrelationId, Reason = f.Reason, Changes = f.Changes });
        await db.SaveChangesAsync(ct);
    }
    public static async Task<AuditDbContext> EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(connection).Options);
        try { await db.Database.UseTransactionAsync(transaction, ct); return db; } catch { await db.DisposeAsync(); throw; }
    }
}
public sealed record AuditQuery(Guid? Actor = null, string? Action = null, string? Resource = null, string? ResourceId = null, string? Result = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Cursor = null, int? PageSize = null);
public sealed record AuditPage(IReadOnlyList<AuditLog> Items, string? NextCursor);
public sealed class AuditSearch(AuditDbContext db, TimeProvider clock)
{
    public async Task<AuditPage> SearchAsync(AuditQuery input, Guid actor, CancellationToken ct)
    {
        var size = Keyset.Size(input.PageSize);
        if (input.Action?.Length > 100 || input.Resource?.Length > 40 || input.ResourceId?.Length > 128 || input.Result is not (null or "Success" or "Denied" or "Failed")) throw new AdminException("VALIDATION_ERROR");
        var hash = Keyset.Fingerprint(new { actor, input.Actor, input.Action, input.Resource, input.ResourceId, input.Result, input.From, input.To });
        var c = Keyset.Decode(input.Cursor, hash, 31); var to = c?.To ?? input.To ?? clock.GetUtcNow().AddSeconds(1); var from = c?.From ?? input.From ?? to.AddDays(-31);
        Keyset.Window(from, to, 31);
        var q = db.Logs.AsNoTracking().Where(x => x.OccurredAt >= from && x.OccurredAt < to);
        if (input.Actor is { } a) q = q.Where(x => x.ActorId == a); if (input.Action != null) q = q.Where(x => x.Action == input.Action);
        if (input.Resource != null) q = q.Where(x => x.ResourceType == input.Resource); if (input.ResourceId != null) q = q.Where(x => x.ResourceId == input.ResourceId);
        if (input.Result != null) q = q.Where(x => x.Result == input.Result);
        if (c != null) q = q.Where(x => x.OccurredAt < c.At || x.OccurredAt == c.At && x.Id.CompareTo(c.Id) < 0);
        var rows = await q.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).Take(size + 1).ToListAsync(ct); var more = rows.Count > size; if (more) rows.RemoveAt(size);
        return new(rows, more ? Keyset.Encode(rows[^1].OccurredAt, rows[^1].Id, from, to, hash) : null);
    }
}
public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AuditDbContext>(o => o.UseNpgsql(config["Platform:PostgreSql:ConnectionString"], n => n.MigrationsHistoryTable("__EFMigrationsHistory", AuditDbContext.Schema)));
        services.AddScoped<IAuditWriter, AuditWriter>(); services.AddScoped<AuditSearch>(); return services;
    }
    public static async Task InitializeAuditModuleAsync(this IServiceProvider services, CancellationToken ct = default)
    { await using var scope = services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync(ct); }
}
public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql("Host=localhost;Database=design_only", n => n.MigrationsHistoryTable("__EFMigrationsHistory", AuditDbContext.Schema)).Options);
}
