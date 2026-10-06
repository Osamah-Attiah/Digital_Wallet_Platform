using DigitalWallet.SharedKernel.Observability;
using System.Security.Cryptography;
using System.Text.Json;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Transactions.Domain;

namespace DigitalWallet.Transactions.Application;

public sealed class TransactionService(ITransactionStore queries, ICustomerEligibilityReader customers,
    ITransactionCoordinator coordinator, IOtpChallengeService otp, TimeProvider clock,
    TransactionSettings settings, IReversalCheckpoint checkpoint)
{
    public async Task<TransactionReceipt> GetAsync(Guid actor, bool any, Guid id, CancellationToken ct) =>
        await queries.GetAsync(await Customer(actor, any, ct), any, id, ct) ?? throw new TransactionsException("TRANSACTION_NOT_FOUND");
    public async Task<TransactionPage> ListAsync(Guid actor, bool any, HistoryQuery input, CancellationToken ct)
    {
        var size = input.PageSize ?? Math.Min(20, settings.MaxPageSize);
        if (size < 1 || size > settings.MaxPageSize ||
            input.Direction is not (null or "Incoming" or "Outgoing") || input.Type is not (null or "InternalTransfer" or "Reversal") ||
            input.Status is not (null or "Completed" or "Reversed") || input.MinAmountMinor < 0 || input.MaxAmountMinor < 0 ||
            input.MinAmountMinor > input.MaxAmountMinor || input.Reference?.Length > 40 || any && input.Direction != null)
            throw new TransactionsException("VALIDATION_ERROR");
        // A cursor carries its original effective dates, so default ranges do not move between pages.
        return await queries.ListAsync(await Customer(actor, any, ct), any, input, size, ct);
    }
    private async Task<Guid?> Customer(Guid actor, bool any, CancellationToken ct) => any ? null :
        (await customers.GetByIdentityUserIdAsync(actor, ct))?.CustomerId;

    public Task<ReversalReceipt> RequestAsync(Guid actor, Guid transferId, string? key, RequestReversal input, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Transactions, OperationName.ReversalRequest, () => RequestAsyncCore(actor, transferId, key, input, ct));
    private Task<ReversalReceipt> RequestAsyncCore(Guid actor, Guid transferId, string? key, RequestReversal input, CancellationToken ct)
    {
        ValidateKey(key); ValidateText(input.Reason);
        var id = Guid.NewGuid(); var hash = Hash(new { transferId, input.Reason });
        return coordinator.ExecuteAsync(async (session, token) =>
        {
            var store = session.Store;
            await store.LockAsync("reversal.request", actor.ToString("D") + ":" + key, token);
            var existing = await store.FindRequestAsync(actor, key!, token);
            if (existing != null)
            {
                if (existing.RequestHash != hash) throw new TransactionsException("IDEMPOTENCY_KEY_REUSED");
                return Receipt(existing);
            }
            var original = await store.OriginalAsync(transferId, true, token) ?? throw new TransactionsException("NOT_REVERSIBLE");
            if (await store.ReversalAsync(original.Id, token) != null) throw new TransactionsException("ALREADY_REVERSED");
            if (await store.HasActiveRequestAsync(original.Id, token)) throw new TransactionsException("REVERSAL_PENDING");
            var request = new ReversalRequest { Id = id, OriginalTransactionId = original.Id, RequestedBy = actor,
                RequestKey = key!, RequestHash = hash, Reason = input.Reason, CreatedAt = Now() };
            store.Add(request); await store.SaveAsync(token); return Receipt(request);
        }, ct);
    }
    public async Task<ReversalReceipt> GetRequestAsync(Guid id, CancellationToken ct) =>
        Receipt(await queries.RequestAsync(id, false, ct) ?? throw new TransactionsException("REVERSAL_NOT_FOUND"));
    public async Task<OtpChallengeIssued> IssueOtpAsync(Guid actor, Guid id, CancellationToken ct)
    {
        var r = await queries.RequestAsync(id, false, ct) ?? throw new TransactionsException("REVERSAL_NOT_FOUND");
        EnsureApprover(r, actor);
        var original = await queries.OriginalAsync(r.OriginalTransactionId, false, ct) ?? throw new TransactionsException("NOT_REVERSIBLE");
        return await otp.CreateAsync(new(actor, id, original.RecipientWalletId, OtpChallengePurpose.ReversalApproval,
            Binding(original)), ct);
    }
    public Task<ReversalReceipt> ApproveAsync(Guid actor, Guid id, string? key, ApproveReversal input, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Transactions, OperationName.ReversalApprove, () => ApproveAsyncCore(actor, id, key, input, ct));
    private async Task<ReversalReceipt> ApproveAsyncCore(Guid actor, Guid id, string? key, ApproveReversal input, CancellationToken ct)
    {
        ValidateKey(key);
        if (input.Note?.Length > 500) throw new TransactionsException("VALIDATION_ERROR");
        var hash = Hash(new { id, input.Note });
        var r = await queries.RequestAsync(id, false, ct) ?? throw new TransactionsException("REVERSAL_NOT_FOUND");
        if (r.Status == "Completed") return ApprovalReplay(r, actor, key!, hash);
        EnsureApprover(r, actor);
        var original = await queries.OriginalAsync(r.OriginalTransactionId, false, ct) ?? throw new TransactionsException("NOT_REVERSIBLE");
        var verification = new VerifyOtpChallenge(input.ChallengeId, actor, id, original.RecipientWalletId,
            OtpChallengePurpose.ReversalApproval, input.Code, Binding(original));
        if (await otp.GetVerificationReceiptAsync(verification, ct) != OtpVerificationReceiptStatus.Verified)
        {
            var result = await otp.VerifyAsync(verification, ct);
            if (result != OtpVerificationStatus.Verified && await otp.GetVerificationReceiptAsync(verification, ct) != OtpVerificationReceiptStatus.Verified) throw new TransactionsException(result switch
            { OtpVerificationStatus.Expired => "OTP_EXPIRED", OtpVerificationStatus.AttemptsExceeded => "OTP_ATTEMPTS_EXCEEDED", _ => "OTP_INVALID" });
        }
        await checkpoint.ReachedAsync("AfterOtp", id, ct);
        var receipt = await coordinator.ExecuteAsync(async (session, token) =>
        {
            var store = session.Store;
            await store.LockAsync("reversal.approve", actor.ToString("D") + ":" + key, token);
            var prior = await store.FindApprovalAsync(actor, key!, token);
            if (prior != null && prior.Id != id) throw new TransactionsException("IDEMPOTENCY_KEY_REUSED");
            var locked = await store.RequestAsync(id, true, token) ?? throw new TransactionsException("REVERSAL_NOT_FOUND");
            if (locked.Status == "Completed") return ApprovalReplay(locked, actor, key!, hash);
            EnsureApprover(locked, actor);
            var fact = await store.OriginalAsync(locked.OriginalTransactionId, true, token) ?? throw new TransactionsException("NOT_REVERSIBLE");
            if (await store.ReversalAsync(fact.Id, token) != null) throw new TransactionsException("ALREADY_REVERSED");
            PostedJournal journal;
            try { journal = await session.Posting.ReverseAsync(fact.JournalId, "InternalTransferReversal", id.ToString("D"), locked.Reason, actor, token); }
            catch (LedgerException ex) when (ex.Code == "INSUFFICIENT_BALANCE") { throw new TransactionsException("INSUFFICIENT_RECOVERY_FUNDS"); }
            await checkpoint.ReachedAsync("AfterLedger", id, token);
            store.Add(new FinancialTransaction { Id = id, PublicReference = "REV-" + id.ToString("N").ToUpperInvariant(),
                Type = "Reversal", TransferId = fact.TransferId, JournalId = journal.Id, ReversalOf = fact.Id,
                SenderCustomerId = fact.RecipientCustomerId, RecipientCustomerId = fact.SenderCustomerId,
                SenderWalletId = fact.RecipientWalletId, RecipientWalletId = fact.SenderWalletId,
                AmountMinor = fact.AmountMinor, FeeMinor = fact.FeeMinor, SenderDebitMinor = fact.AmountMinor,
                RecipientCreditMinor = fact.SenderDebitMinor, Currency = fact.Currency,
                MaskedSenderName = fact.MaskedRecipientName, MaskedRecipientName = fact.MaskedSenderName, OccurredAt = Now() });
            locked.Status = "Completed"; locked.DecidedBy = actor; locked.DecidedAt = Now(); locked.DecisionNote = input.Note;
            locked.ApprovalKey = key; locked.ApprovalHash = hash; locked.OtpChallengeId = input.ChallengeId; locked.ReversalTransactionId = id;
            await store.SaveAsync(token); await checkpoint.ReachedAsync("BeforeCommit", id, token); return Receipt(locked);
        }, ct);
        await checkpoint.ReachedAsync("AfterCommit", id, ct);
        return receipt;
    }
    public Task<ReversalReceipt> RejectAsync(Guid actor, Guid id, RejectReversal input, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Transactions, OperationName.ReversalReject, () => RejectAsyncCore(actor, id, input, ct));
    private Task<ReversalReceipt> RejectAsyncCore(Guid actor, Guid id, RejectReversal input, CancellationToken ct)
    {
        ValidateText(input.Reason);
        return coordinator.ExecuteAsync(async (session, token) =>
        {
            var r = await session.Store.RequestAsync(id, true, token) ?? throw new TransactionsException("REVERSAL_NOT_FOUND");
            EnsureApprover(r, actor);
            r.Status = "Rejected"; r.DecidedBy = actor; r.DecidedAt = Now(); r.DecisionNote = input.Reason;
            await session.Store.SaveAsync(token); return Receipt(r);
        }, ct);
    }
    private DateTimeOffset Now() { var now = clock.GetUtcNow(); return now.AddTicks(-(now.Ticks % 10)); }
    private static TransferOtpDetails Binding(FinancialTransaction x) => new(x.SenderWalletId, x.AmountMinor, x.FeeMinor, x.Currency);
    private static void EnsureApprover(ReversalRequest r, Guid actor)
    {
        if (r.RequestedBy == actor) throw new TransactionsException("SELF_APPROVAL_FORBIDDEN");
        if (r.Status != "Pending") throw new TransactionsException("STATE_CONFLICT");
    }
    private static ReversalReceipt ApprovalReplay(ReversalRequest r, Guid actor, string key, string hash)
    {
        if (r.DecidedBy != actor || r.ApprovalKey != key) throw new TransactionsException("ALREADY_REVERSED");
        if (r.ApprovalHash != hash) throw new TransactionsException("IDEMPOTENCY_KEY_REUSED");
        return Receipt(r);
    }
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static void ValidateKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) throw new TransactionsException("IDEMPOTENCY_KEY_REQUIRED");
        if (key.Length > 128 || key.Any(c => c < 33 || c > 126)) throw new TransactionsException("VALIDATION_ERROR");
    }
    private static void ValidateText(string value)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 500) throw new TransactionsException("VALIDATION_ERROR"); }
    private static ReversalReceipt Receipt(ReversalRequest r) => new(r.Id, r.OriginalTransactionId, r.Status, r.Reason,
        r.CreatedAt, r.RequestedBy, r.DecidedBy, r.DecidedAt, r.DecisionNote, r.ReversalTransactionId);
}
