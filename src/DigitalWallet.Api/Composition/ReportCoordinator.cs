using DigitalWallet.Reporting;
using DigitalWallet.Reporting.Contracts;
using DigitalWallet.Messaging;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.Audit;
using DigitalWallet.Audit.Contracts;
using DigitalWallet.SharedKernel.Administration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace DigitalWallet.Api.Composition;

public sealed class ReportCoordinator(IConfiguration config, TimeProvider clock, ReportOptions options, IReportCheckpoint checkpoint) : IReportCoordinator
{
    private ReportingDbContext Context(NpgsqlConnection c) => new(new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(c).Options);
    public async Task<ReportReceipt> CreateAsync(ReportCreation c, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(config["Platform:PostgreSql:ConnectionString"]); await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct); await using var db = Context(connection); await db.Database.UseTransactionAsync(tx, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({c.Owner.ToString("D") + ":" + c.Key},1010))", ct);
        var prior = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerId == c.Owner && x.IdempotencyKey == c.Key, ct);
        if (prior != null) { if (prior.Fingerprint != c.Fingerprint) throw new AdminException("IDEMPOTENCY_KEY_REUSED", 409); await tx.CommitAsync(ct); return prior.Receipt(); }
        var now = clock.GetUtcNow(); now = now.AddTicks(-(now.Ticks % 10));
        var j = new ReportJob { Id = Guid.NewGuid(), OwnerId = c.Owner, IdempotencyKey = c.Key, Fingerprint = c.Fingerprint, Type = c.Request.Type, From = c.Request.From, To = c.Request.To, IncludesPii = c.Request.IncludePii, CreatedAt = now, ExpiresAt = now.AddHours(options.RetentionHours), CorrelationId = c.CorrelationId };
        db.Jobs.Add(j); await db.SaveChangesAsync(ct); await checkpoint.ReachedAsync("AfterReportJob", j.Id, ct);
        await using var outbox = await OutboxWriter.EnlistAsync(connection, tx, ct); await outbox.AppendAsync(WalletEvents.Report(j.Id, j.CreatedAt, j.CorrelationId), ct);
        await checkpoint.ReachedAsync("AfterReportOutbox", j.Id, ct);
        await using var audit = await AuditWriter.EnlistAsync(connection, tx, ct);
        await new AuditWriter(audit, clock).AppendAsync(new(c.Owner, "Staff", "report.create", "Report", j.Id.ToString("D"), "Success", j.CorrelationId, "AdministrativeReview", System.Text.Json.JsonSerializer.Serialize(new { pii = j.IncludesPii })), ct);
        await tx.CommitAsync(ct); return j.Receipt();
    }
    public async Task FinishAsync(ReportFinish f, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(config["Platform:PostgreSql:ConnectionString"]); await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct); await using var db = Context(connection); await db.Database.UseTransactionAsync(tx, ct);
        var rows = await db.Jobs.FromSqlInterpolated($"SELECT * FROM reporting.jobs WHERE \"Id\"={f.Id} FOR UPDATE").ToListAsync(ct); var j = rows.Single();
        if (j.LeaseToken != f.Lease || j.Status != "Running" || j.LeaseUntil <= clock.GetUtcNow()) throw new ReportLeaseBusyException();
        j.Status = j.ExpiresAt <= clock.GetUtcNow() ? "Expired" : f.Status; j.RowCount = f.RowCount; j.FailureCode = f.FailureCode; j.LeaseToken = null; j.LeaseUntil = null;
        db.Inbox.Add(new() { EventId = WalletEvents.Report(j.Id, j.CreatedAt, j.CorrelationId).EventId, JobId = j.Id, CompletedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct); await checkpoint.ReachedAsync("BeforeReportCommit", j.Id, ct);
        await using var audit = await AuditWriter.EnlistAsync(connection, tx, ct);
        await new AuditWriter(audit, clock).AppendAsync(new(j.OwnerId, "Worker", "report.execute", "Report", j.Id.ToString("D"), j.Status == "Completed" ? "Success" : "Failed", j.CorrelationId, j.FailureCode == "PERMISSION_REVOKED" ? "PermissionRevoked" : "AdministrativeReview", System.Text.Json.JsonSerializer.Serialize(new { rowCount = j.RowCount ?? 0, pii = j.IncludesPii })), ct);
        await tx.CommitAsync(ct);
    }
}
