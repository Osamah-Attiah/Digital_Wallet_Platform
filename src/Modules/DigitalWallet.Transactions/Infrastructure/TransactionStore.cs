using System.Security.Cryptography;
using System.Text.Json;
using DigitalWallet.Transactions.Application;
using DigitalWallet.Transactions.Domain;
using DigitalWallet.Transfers.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Transactions.Infrastructure;
public sealed class TransactionStore(TransactionsDbContext db, TimeProvider clock, TransactionSettings settings) : ITransactionStore
{
    public async Task RecordAsync(CompletedTransfer x, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO transactions.transactions ("Id", "PublicReference", "Type", "TransferId", "JournalId", "SenderCustomerId", "RecipientCustomerId",
                "SenderWalletId", "RecipientWalletId", "AmountMinor", "FeeMinor", "SenderDebitMinor", "RecipientCreditMinor", "Currency",
                "MaskedSenderName", "MaskedRecipientName", "OccurredAt")
            VALUES ({x.Id}, {x.PublicReference}, 'InternalTransfer', {x.Id}, {x.JournalId}, {x.SenderCustomerId}, {x.RecipientCustomerId},
                {x.SenderWalletId}, {x.RecipientWalletId}, {x.AmountMinor}, {x.FeeMinor}, {x.TotalDebitMinor}, {x.AmountMinor}, {x.Currency},
                {x.MaskedSenderName}, {x.MaskedRecipientName}, {x.CompletedAt}) ON CONFLICT ("Id") DO NOTHING
            """, ct);
        var row = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == x.Id, ct);
        if (row.Type != "InternalTransfer" || row.JournalId != x.JournalId || row.SenderCustomerId != x.SenderCustomerId ||
            row.RecipientCustomerId != x.RecipientCustomerId || row.SenderWalletId != x.SenderWalletId || row.RecipientWalletId != x.RecipientWalletId ||
            row.AmountMinor != x.AmountMinor || row.FeeMinor != x.FeeMinor || row.SenderDebitMinor != x.TotalDebitMinor || row.Currency != x.Currency ||
            row.PublicReference != x.PublicReference || row.MaskedSenderName != x.MaskedSenderName || row.MaskedRecipientName != x.MaskedRecipientName ||
            row.OccurredAt != x.CompletedAt)
            throw new TransactionsException("STATE_CONFLICT");
    }
    public Task LockAsync(string scope, string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtextextended({JsonSerializer.Serialize(new[] { scope, key })}, 8108))", ct);
    public async Task<FinancialTransaction?> OriginalAsync(Guid id, bool locked, CancellationToken ct) => locked
        ? (await db.Transactions.FromSqlInterpolated($"SELECT * FROM transactions.transactions WHERE \"Id\" = {id} AND \"Type\" = 'InternalTransfer' FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault()
        : await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Type == "InternalTransfer", ct);
    public async Task<ReversalRequest?> RequestAsync(Guid id, bool locked, CancellationToken ct) => locked
        ? (await db.ReversalRequests.FromSqlInterpolated($"SELECT * FROM transactions.reversal_requests WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
        : await db.ReversalRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<ReversalRequest?> FindRequestAsync(Guid actor, string key, CancellationToken ct) =>
        db.ReversalRequests.AsNoTracking().SingleOrDefaultAsync(x => x.RequestedBy == actor && x.RequestKey == key, ct);
    public Task<ReversalRequest?> FindApprovalAsync(Guid actor, string key, CancellationToken ct) =>
        db.ReversalRequests.AsNoTracking().SingleOrDefaultAsync(x => x.DecidedBy == actor && x.ApprovalKey == key, ct);
    public Task<bool> HasActiveRequestAsync(Guid id, CancellationToken ct) =>
        db.ReversalRequests.AnyAsync(x => x.OriginalTransactionId == id && (x.Status == "Pending" || x.Status == "Completed"), ct);
    public Task<Guid?> ReversalAsync(Guid id, CancellationToken ct) => db.Transactions.AsNoTracking().Where(x => x.ReversalOf == id).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
    public void Add(ReversalRequest request) => db.ReversalRequests.Add(request);
    public void Add(FinancialTransaction transaction) => db.Transactions.Add(transaction);
    public async Task SaveAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);
    private IQueryable<Row> Visible(Guid? customer, bool any) =>
        from t in db.Transactions.AsNoTracking()
        where any || customer != null && (t.SenderCustomerId == customer || t.RecipientCustomerId == customer)
        select new Row { Transaction = t, ReversalId = db.Transactions.Where(r => r.ReversalOf == t.Id).Select(r => (Guid?)r.Id).SingleOrDefault() };
    public async Task<TransactionReceipt?> GetAsync(Guid? customer, bool any, Guid id, CancellationToken ct)
    {
        var row = await Visible(customer, any).SingleOrDefaultAsync(x => x.Transaction.Id == id, ct);
        return row is null ? null : Receipt(row, customer);
    }
    public async Task<TransactionPage> ListAsync(Guid? customer, bool any, HistoryQuery input, int size, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { customer, any, input.From, input.To, input.Direction, input.Type, input.Status, input.MinAmountMinor, input.MaxAmountMinor, input.Reference })));
        var now = clock.GetUtcNow();
        var to = input.To ?? now.AddTicks(10 - now.Ticks % 10);
        var from = input.From ?? (to - DateTimeOffset.MinValue < TimeSpan.FromDays(settings.MaxHistoryDays)
            ? DateTimeOffset.MinValue : to.AddDays(-settings.MaxHistoryDays));
        Cursor? cursor = null;
        if (input.Cursor is not null)
        {
            try
            {
                if (input.Cursor.Length > 1024) throw new FormatException();
                cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(input.Cursor)) ?? throw new FormatException();
                if (cursor.Hash != hash || cursor.Id == Guid.Empty || cursor.At.Offset != TimeSpan.Zero || cursor.From.Offset != TimeSpan.Zero ||
                    cursor.To.Offset != TimeSpan.Zero || cursor.From >= cursor.To || cursor.To - cursor.From > TimeSpan.FromDays(settings.MaxHistoryDays) ||
                    cursor.At < cursor.From || cursor.At >= cursor.To || input.From != null && input.From != cursor.From || input.To != null && input.To != cursor.To)
                    throw new FormatException();
                from = cursor.From; to = cursor.To;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            { throw new TransactionsException("INVALID_CURSOR"); }
        }
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from >= to)
            throw new TransactionsException("VALIDATION_ERROR");
        if (to - from > TimeSpan.FromDays(settings.MaxHistoryDays)) throw new TransactionsException("DATE_RANGE_TOO_LARGE");
        var query = Visible(customer, any).Where(x => x.Transaction.OccurredAt >= from && x.Transaction.OccurredAt < to);
        if (input.Direction == "Incoming") query = query.Where(x => x.Transaction.RecipientCustomerId == customer);
        if (input.Direction == "Outgoing") query = query.Where(x => x.Transaction.SenderCustomerId == customer);
        if (input.Type != null) query = query.Where(x => x.Transaction.Type == input.Type);
        if (input.Status == "Reversed") query = query.Where(x => x.ReversalId != null);
        if (input.Status == "Completed") query = query.Where(x => x.ReversalId == null);
        if (input.MinAmountMinor is { } min) query = query.Where(x => x.Transaction.AmountMinor >= min);
        if (input.MaxAmountMinor is { } max) query = query.Where(x => x.Transaction.AmountMinor <= max);
        if (input.Reference != null) query = query.Where(x => x.Transaction.PublicReference == input.Reference);
        if (cursor is { } value) query = query.Where(x => x.Transaction.OccurredAt < value.At ||
            x.Transaction.OccurredAt == value.At && x.Transaction.Id.CompareTo(value.Id) < 0);
        var rows = await query.OrderByDescending(x => x.Transaction.OccurredAt).ThenByDescending(x => x.Transaction.Id).Take(size + 1).ToListAsync(ct);
        var more = rows.Count > size; if (more) rows.RemoveAt(size);
        return new(rows.Select(x => Receipt(x, customer)).ToArray(), more ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(rows[^1].Transaction.OccurredAt, rows[^1].Transaction.Id, from, to, hash))) : null);
    }
    private static TransactionReceipt Receipt(Row row, Guid? customer)
    {
        var t = row.Transaction; var outgoing = customer == t.SenderCustomerId;
        return new(t.Id, t.PublicReference, t.Type, row.ReversalId != null ? "Reversed" : "Completed",
            customer is null ? null : outgoing ? "Outgoing" : "Incoming", t.AmountMinor, t.FeeMinor,
            t.SenderDebitMinor, t.RecipientCreditMinor, customer is null ? null : outgoing ? -t.SenderDebitMinor : t.RecipientCreditMinor,
            t.Currency, t.MaskedSenderName, t.MaskedRecipientName, t.OccurredAt, t.TransferId, t.ReversalOf, row.ReversalId);
    }
    private sealed class Row { public FinancialTransaction Transaction { get; set; } = null!; public Guid? ReversalId { get; set; } }
    private sealed record Cursor(DateTimeOffset At, Guid Id, DateTimeOffset From, DateTimeOffset To, string Hash);
}
