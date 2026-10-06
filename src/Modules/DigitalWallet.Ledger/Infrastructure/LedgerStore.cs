using System.Data;
using System.Text.Json;
using DigitalWallet.Ledger.Application;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DigitalWallet.Ledger.Infrastructure;

public sealed class LedgerStore(LedgerDbContext db) : ILedgerStore, ILedgerAccounts, ILedgerReader, ITransferLedger
{
    public Task<Guid> EnsureWalletAccountAsync(Guid walletId, string currency, CancellationToken ct = default)
    {
        if (walletId == Guid.Empty) throw new LedgerException("VALIDATION_ERROR", "A wallet identifier is required.");
        return EnsureAccountAsync(AccountKind.CustomerWalletLiability, walletId, currency, ct);
    }

    public Task<Guid> EnsureSystemAccountAsync(SystemAccount account, string currency, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(account)) throw new LedgerException("VALIDATION_ERROR", "Unknown system account.");
        return EnsureAccountAsync(Enum.Parse<AccountKind>(account.ToString()), null, currency, ct);
    }

    private async Task<Guid> EnsureAccountAsync(AccountKind kind, Guid? wallet, string currency, CancellationToken ct)
    {
        if (!LedgerRules.Currencies.Contains(currency)) throw new LedgerException("VALIDATION_ERROR", "Unsupported ISO-4217 currency.");
        var id = Guid.NewGuid();
        var code = wallet.HasValue ? $"{kind}:{wallet:D}:{currency}" : $"{kind}:{currency}";
        var side = kind is AccountKind.CustomerWalletLiability or AccountKind.PlatformFeeRevenue ? "Credit" : "Debit";
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ledger.accounts ("Id", "WalletId", "Code", "Currency", "Kind", "NormalSide")
            VALUES ({id}, {wallet}, {code}, {currency}, {kind.ToString()}, {side}) ON CONFLICT DO NOTHING
            """, ct);
        var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Kind == kind && x.WalletId == wallet && x.Currency == currency, ct);
        // The account-insert trigger creates its zero projection in the same transaction.
        await tx.CommitAsync(ct);
        return account.Id;
    }

    public async Task<WalletBalance?> GetWalletBalanceAsync(Guid walletId, string currency, CancellationToken ct = default) =>
        await (from a in db.Accounts.AsNoTracking()
               join b in db.Balances.AsNoTracking() on a.Id equals b.AccountId
               where a.WalletId == walletId && a.Currency == currency
               select new WalletBalance(walletId, a.Id, currency, b.BalanceMinor, b.BalanceMinor, b.Version, b.UpdatedAt))
            .SingleOrDefaultAsync(ct);

    public async Task<PostedJournal> PostAsync(PostJournal request, string fingerprint, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await PostOnceAsync(request, fingerprint, ct); }
            catch (PostgresException ex) when (ex.SqlState is "40P01" or "40001" && attempt < 3 && db.Database.CurrentTransaction is null)
            {
                DigitalWallet.SharedKernel.Observability.WalletTelemetry.Retry();
                // Retry the entire transaction, preserving the source key and fingerprint.
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct);
            }
            catch (PostgresException ex) when (ex.ConstraintName == "IX_journals_ReversalOf")
            { throw new LedgerException("ALREADY_REVERSED", "The journal already has a reversal."); }
            catch (PostgresException ex) when (ex.SqlState == "22003")
            { throw new LedgerException("AMOUNT_OUT_OF_RANGE", "The resulting balance exceeds the supported range."); }
            catch (PostgresException ex) when (ex.ConstraintName == "ledger_insufficient_balance")
            { throw new LedgerException("INSUFFICIENT_BALANCE", "The wallet has insufficient available balance."); }
            catch (PostgresException ex) when (ex.ConstraintName == "ledger_invalid_reversal")
            { throw new LedgerException("INVALID_REVERSAL", "A reversal must exactly reverse a posted original journal."); }
        }
    }

    private async Task<PostedJournal> PostOnceAsync(PostJournal request, string fingerprint, CancellationToken ct)
    {
        var owned = db.Database.CurrentTransaction is null;
        var tx = db.Database.CurrentTransaction ?? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var savepoint = "ledger_" + Guid.NewGuid().ToString("N");
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            var sourceKey = JsonSerializer.Serialize(new[] { request.SourceType, request.SourceId });
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({sourceKey}, 0))", ct);
            var existing = await db.Journals.AsNoTracking().SingleOrDefaultAsync(x => x.SourceType == request.SourceType && x.SourceId == request.SourceId, ct);
            if (existing is not null)
            {
                if (existing.RequestHash != fingerprint) throw new LedgerException("IDEMPOTENCY_KEY_REUSED", "The source key was already used with different content.");
                if (owned) await tx.CommitAsync(ct); else await tx.ReleaseSavepointAsync(savepoint, ct);
                return Map(existing);
            }

            if (request.ReversalOf is { } originalId)
            {
                // Full reversals serialize on their original before taking any balance lock.
                var originals = await db.Journals.FromSqlInterpolated($"SELECT * FROM ledger.journals WHERE \"Id\" = {originalId} FOR UPDATE")
                    .AsNoTracking().ToListAsync(ct);
                if (originals.Count == 0) throw new LedgerException("JOURNAL_NOT_FOUND", "The journal was not found.");
                if (await db.Journals.AnyAsync(x => x.ReversalOf == originalId, ct))
                    throw new LedgerException("ALREADY_REVERSED", "The journal already has a reversal.");
            }

            var ids = request.Entries.Select(x => x.AccountId).Distinct().ToArray();
            var accounts = await db.Accounts.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            if (accounts.Count != ids.Length || accounts.Any(x => x.Currency != request.Currency))
                throw new LedgerException("VALIDATION_ERROR", "All accounts must exist and use the journal currency.");
            // PostgreSQL UUID ordering is also used by the posting trigger, independent of caller line order.
            var balances = await db.Balances.FromSqlInterpolated($"""
                SELECT * FROM ledger.account_balances WHERE "AccountId" = ANY({ids}) ORDER BY "AccountId" FOR UPDATE
                """).AsNoTracking().ToListAsync(ct);
            if (balances.Count != accounts.Count) throw new LedgerException("LEDGER_UNAVAILABLE", "An account projection is missing.");
            foreach (var account in accounts)
            {
                var delta = request.Entries.Where(x => x.AccountId == account.Id)
                    .Sum(x => x.Side.ToString() == account.NormalSide.ToString() ? (decimal)x.AmountMinor : -(decimal)x.AmountMinor);
                var result = balances.Single(x => x.AccountId == account.Id).BalanceMinor + delta;
                if (account.Kind == AccountKind.CustomerWalletLiability && result < 0)
                    throw new LedgerException("INSUFFICIENT_BALANCE", "The wallet has insufficient available balance.");
                if (result > long.MaxValue || result < long.MinValue)
                    throw new LedgerException("AMOUNT_OUT_OF_RANGE", "The resulting balance exceeds the supported range.");
            }
            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ledger.journals ("Id", "SourceType", "SourceId", "RequestHash", "Currency", "Reason", "ActorId", "ReversalOf", "Status")
                VALUES ({id}, {request.SourceType}, {request.SourceId}, {fingerprint}, {request.Currency}, {request.Reason}, {request.ActorId}, {request.ReversalOf}, 'Draft')
                """, ct);
            foreach (var line in request.Entries)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ledger.entries ("Id", "JournalId", "AccountId", "Side", "AmountMinor", "Currency")
                    VALUES ({Guid.NewGuid()}, {id}, {line.AccountId}, {line.Side.ToString()}, {line.AmountMinor}, {request.Currency})
                    """, ct);
            // This transition validates and projects inside PostgreSQL, including for direct SQL callers.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ledger.journals SET \"Status\" = 'Posted' WHERE \"Id\" = {id}", ct);
            var posted = await db.Journals.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            if (owned) await tx.CommitAsync(ct); else await tx.ReleaseSavepointAsync(savepoint, ct);
            return Map(posted);
        }
        catch
        {
            try
            {
                if (owned) await tx.RollbackAsync(CancellationToken.None);
                else await tx.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            }
            catch { /* Preserve failure for coordinator recovery. */ }
            db.ChangeTracker.Clear();
            throw;
        }
        finally { if (owned) await tx.DisposeAsync(); }
    }

    public Task<TransferLedgerAccounts> LockAsync(Guid operationId, Guid senderWalletId, Guid targetWalletId, string currency, bool includeFee, CancellationToken ct) =>
        DigitalWallet.SharedKernel.Transactions.ParticipantSavepoint.RunAsync(
            Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(db.Database.CurrentTransaction
                ?? throw new InvalidOperationException("A coordinator transaction is required.")), async () =>
        {
            var source = JsonSerializer.Serialize(new[] { "InternalTransfer", operationId.ToString("D") });
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({source}, 0))", ct);
            var accounts = await db.Accounts.AsNoTracking().Where(x => x.Currency == currency &&
                (x.WalletId == senderWalletId || x.WalletId == targetWalletId || includeFee && x.Kind == AccountKind.PlatformFeeRevenue)).ToListAsync(ct);
            var sender = accounts.SingleOrDefault(x => x.WalletId == senderWalletId);
            var target = accounts.SingleOrDefault(x => x.WalletId == targetWalletId);
            var fee = accounts.SingleOrDefault(x => x.Kind == AccountKind.PlatformFeeRevenue);
            if (sender is null || target is null || includeFee && fee is null)
                throw new LedgerException("LEDGER_UNAVAILABLE", "Transfer accounts are unavailable.");
            var ids = accounts.Select(x => x.Id).ToArray();
            var balances = await db.Balances.FromSqlInterpolated($"SELECT * FROM ledger.account_balances WHERE \"AccountId\" = ANY({ids}) ORDER BY \"AccountId\" FOR UPDATE")
                .AsNoTracking().ToListAsync(ct);
            if (balances.Count != accounts.Count) throw new LedgerException("LEDGER_UNAVAILABLE", "Transfer projections are unavailable.");
            return new TransferLedgerAccounts(sender.Id, target.Id, fee?.Id ?? Guid.Empty, balances.Single(x => x.AccountId == sender.Id).BalanceMinor);
        }, ct);

    public async Task<PostJournal?> ReadOriginalAsync(Guid id, CancellationToken ct)
    {
        var journal = await db.Journals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Status == JournalStatus.Posted, ct);
        if (journal is null) return null;
        var lines = await db.Entries.AsNoTracking().Where(x => x.JournalId == id).ToListAsync(ct);
        return new(journal.SourceType, journal.SourceId, journal.Currency,
            lines.Select(x => new Posting(x.AccountId, (PostingSide)x.Side, x.AmountMinor)).ToArray(), journal.Reason, journal.ActorId, journal.ReversalOf);
    }

    public async Task<ReconciliationReport> ReconcileAsync(IReadOnlyList<WalletLedgerReference> wallets, CancellationToken ct)
    {
        // Both comparisons observe the same committed snapshot; concurrent postings cannot create false drift.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var asOf = DateTimeOffset.UtcNow;
        var findings = new List<ReconciliationFinding>();
        var journals = await db.Database.SqlQueryRaw<Guid>("""
            SELECT j."Id" AS "Value" FROM ledger.journals j
            LEFT JOIN ledger.entries e ON e."JournalId" = j."Id"
            GROUP BY j."Id"
            HAVING j."Status" <> 'Posted' OR count(e."Id") < 2 OR
                coalesce(sum(CASE WHEN e."Side" = 'Debit' THEN e."AmountMinor"::numeric ELSE -e."AmountMinor"::numeric END), 0) <> 0
                OR bool_or(e."Currency" <> j."Currency")
            """).ToListAsync(ct);
        findings.AddRange(journals.Select(x => new ReconciliationFinding("UnbalancedJournal", x)));
        var balances = await db.Database.SqlQueryRaw<BalanceComparison>("""
            SELECT a."Id" AS "AccountId", b."BalanceMinor"::numeric AS "Actual",
                coalesce(sum(CASE WHEN j."Status" = 'Posted' THEN
                    CASE WHEN e."Side" = a."NormalSide" THEN e."AmountMinor"::numeric ELSE -e."AmountMinor"::numeric END
                    ELSE 0 END), 0) AS "Expected"
            FROM ledger.accounts a LEFT JOIN ledger.account_balances b ON b."AccountId" = a."Id"
            LEFT JOIN ledger.entries e ON e."AccountId" = a."Id"
            LEFT JOIN ledger.journals j ON j."Id" = e."JournalId"
            GROUP BY a."Id", b."BalanceMinor"
            """).ToListAsync(ct);
        findings.AddRange(balances.Where(x => x.Actual != x.Expected)
            .Select(x => new ReconciliationFinding("BalanceDrift", x.AccountId, x.Expected, x.Actual)));
        var links = await db.Accounts.AsNoTracking().Where(x => x.WalletId != null)
            .Select(x => new WalletLedgerReference(x.WalletId!.Value, x.Currency)).ToListAsync(ct);
        var provisioned = links.ToHashSet();
        findings.AddRange(wallets.Where(x => !provisioned.Contains(x))
            .Select(x => new ReconciliationFinding("WalletWithoutAccount", x.WalletId)));
        await tx.CommitAsync(ct);
        return new(asOf, findings);
    }

    private static PostedJournal Map(JournalEntry journal) => new(journal.Id, journal.SourceType, journal.SourceId,
        journal.Currency, journal.PostedAt ?? throw new InvalidOperationException("Uncommitted journal escaped the posting boundary."));
    private sealed class BalanceComparison
    {
        public Guid AccountId { get; set; }
        public decimal? Actual { get; set; }
        public decimal Expected { get; set; }
    }
}
