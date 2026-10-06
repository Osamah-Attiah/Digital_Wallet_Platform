using System.Text.Json;
using DigitalWallet.Transfers.Application;
using DigitalWallet.Transfers.Contracts;
using DigitalWallet.Transfers.Domain;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Transfers.Infrastructure;

public sealed class TransfersStore(TransfersDbContext db) : ITransferStore, ITransferQueries, ICompletedTransferReader
{
    public async Task<CompletedTransfer?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Status == TransferStatus.Completed, ct);
        return row is null ? null : Fact(row);
    }
    public async Task<IReadOnlyList<CompletedTransfer>> ListAsync(Guid? afterId, int size, CancellationToken ct) =>
        (await db.Transfers.AsNoTracking().Where(x => x.Status == TransferStatus.Completed &&
            (afterId == null || x.Id.CompareTo(afterId.Value) > 0)).OrderBy(x => x.Id).Take(size).ToListAsync(ct)).Select(Fact).ToArray();
    private static CompletedTransfer Fact(Transfer x) => new(x.Id, x.PublicReference, x.OwnerCustomerId, x.TargetCustomerId,
        x.SenderWalletId, x.TargetWalletId, x.JournalId!.Value, x.AmountMinor, x.FeeMinor, x.TotalDebitMinor, x.Currency,
        x.MaskedSenderName, x.MaskedRecipientName, x.CompletedAt!.Value);
    public Task LockKeyAsync(Guid owner, string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtextextended({JsonSerializer.Serialize(new { Owner = owner, Scope = "transfer.create", Key = key })}, 7107))", ct);
    public async Task<TransferIdempotency?> FindKeyAsync(Guid owner, string key, CancellationToken ct) =>
        (await db.Idempotency.FromSqlInterpolated($"SELECT * FROM transfers.idempotency WHERE \"OwnerCustomerId\" = {owner} AND \"Scope\" = 'transfer.create' AND \"Key\" = {key} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
    public async Task<TransferIdempotency> GetKeyAsync(Guid transferId, CancellationToken ct) =>
        (await db.Idempotency.FromSqlInterpolated($"SELECT * FROM transfers.idempotency WHERE \"TransferId\" = {transferId} FOR UPDATE").ToListAsync(ct)).Single();
    public async Task<TransferQuote?> QuoteAsync(Guid owner, Guid id, CancellationToken ct) =>
        (await db.Quotes.FromSqlInterpolated($"SELECT * FROM transfers.quotes WHERE \"Id\" = {id} AND \"OwnerCustomerId\" = {owner} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
    public Task<bool> HasIntentAsync(Guid quoteId, CancellationToken ct) => db.Transfers.AnyAsync(x => x.QuoteId == quoteId, ct);
    public async Task<Transfer?> TransferAsync(Guid identity, Guid id, CancellationToken ct) =>
        (await db.Transfers.FromSqlInterpolated($"SELECT * FROM transfers.transfers WHERE \"Id\" = {id} AND \"OwnerIdentityUserId\" = {identity} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
    public void Add(TransferQuote quote) => db.Quotes.Add(quote);
    public void Add(Transfer transfer, TransferIdempotency idempotency) { db.Transfers.Add(transfer); db.Idempotency.Add(idempotency); }
    public async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new TransferException("CONCURRENCY_CONFLICT"); }
    }
    public Task<Transfer?> OwnedAsync(Guid identity, Guid id, CancellationToken ct) => db.Transfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerIdentityUserId == identity, ct);
    public async Task<TransferReceipt?> GetAsync(Guid? customer, Guid id, bool any, CancellationToken ct)
    {
        var t = await Visible(customer, any).SingleOrDefaultAsync(x => x.Id == id, ct);
        return t == null ? null : TransferService.Receipt(t);
    }
    public async Task<TransferPage> ListAsync(Guid? customer, bool any, string? cursor, int size, CancellationToken ct)
    {
        var query = Visible(customer, any);
        if (cursor != null)
        {
            Cursor value;
            try
            {
                if (cursor.Length > 512) throw new FormatException();
                value = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor)) ?? throw new FormatException();
                if (value.Id == Guid.Empty || value.At.Offset != TimeSpan.Zero) throw new FormatException();
            }
            catch { throw new TransferException("VALIDATION_ERROR"); }
            query = query.Where(x => x.CreatedAt < value.At || x.CreatedAt == value.At && x.Id.CompareTo(value.Id) < 0);
        }
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1).ToListAsync(ct);
        var more = rows.Count > size;
        if (more) rows.RemoveAt(size);
        return new(rows.Select(TransferService.Receipt).ToArray(), more ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(rows[^1].CreatedAt, rows[^1].Id))) : null);
    }
    private IQueryable<Transfer> Visible(Guid? customer, bool any) => db.Transfers.AsNoTracking().Where(x => any || customer != null && (x.OwnerCustomerId == customer || x.TargetCustomerId == customer));
    private sealed record Cursor(DateTimeOffset At, Guid Id);
}
