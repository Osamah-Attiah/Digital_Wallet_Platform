using DigitalWallet.Transactions.Contracts;
using DigitalWallet.SharedKernel.Administration;
using Microsoft.EntityFrameworkCore;
namespace DigitalWallet.Transactions.Infrastructure;

public sealed class AdminTransactionReader(TransactionsDbContext db, TimeProvider clock) : IAdminTransactionReader
{
    public async Task<IReadOnlyList<ExportTransaction>> ExportAsync(DateTimeOffset from, DateTimeOffset to, int limit, bool reversalsOnly, CancellationToken ct) =>
        await db.Transactions.AsNoTracking().Where(x => x.OccurredAt >= from && x.OccurredAt < to && (!reversalsOnly || x.Type == "Reversal"))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Take(limit).Select(x => new ExportTransaction(x.Id, x.PublicReference, x.Type, x.AmountMinor, x.FeeMinor, x.Currency, x.MaskedSenderName, x.MaskedRecipientName, x.OccurredAt)).ToListAsync(ct);
    public Task<long> PendingReversalsAsync(CancellationToken ct) => db.ReversalRequests.LongCountAsync(x => x.Status == "Pending", ct);
    public async Task<ReversalPage> ReversalsAsync(string? status, string? cursor, int? pageSize, Guid actor, CancellationToken ct)
    {
        if (status is not (null or "Pending" or "Completed" or "Rejected")) throw new AdminException("VALIDATION_ERROR");
        var size = Keyset.Size(pageSize); var hash = Keyset.Fingerprint(new { actor, status }); var c = Keyset.Decode(cursor, hash, 36600);
        var from = c?.From ?? DateTimeOffset.UnixEpoch; var to = c?.To ?? clock.GetUtcNow().AddSeconds(1);
        var q = db.ReversalRequests.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to && (status == null || x.Status == status));
        if (c != null) q = q.Where(x => x.CreatedAt < c.At || x.CreatedAt == c.At && x.Id.CompareTo(c.Id) < 0);
        var rows = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1).ToListAsync(ct); var more = rows.Count > size; if (more) rows.RemoveAt(size);
        return new(rows.Select(x => new AdminReversal(x.Id, x.OriginalTransactionId, x.Status, x.CreatedAt, x.RequestedBy, x.DecidedBy, x.DecidedAt)).ToArray(), more ? Keyset.Encode(rows[^1].CreatedAt, rows[^1].Id, from, to, hash) : null);
    }
}
