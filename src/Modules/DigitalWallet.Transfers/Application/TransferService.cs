using DigitalWallet.SharedKernel.Observability;
using System.Security.Cryptography;
using System.Text.Json;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.Transfers.Contracts;
using DigitalWallet.Transfers.Domain;
using DigitalWallet.Wallets.Contracts;

namespace DigitalWallet.Transfers.Application;

public sealed class TransferService(ITransferCoordinator coordinator, ITransferQueries queries, ICustomerEligibilityReader customers,
    IOtpChallengeService otp, TimeProvider clock, TransferSettings settings, ITransferCheckpoint checkpoint) : ITransferReader
{
    public Task<QuoteResponse> QuoteAsync(Guid identity, QuoteRequest request, CancellationToken ct) => coordinator.ExecuteAsync(async (s, token) =>
    {
        if (request.BeneficiaryId == Guid.Empty || request.AmountMinor <= 0 || request.Currency != "SAR") throw Error("VALIDATION_ERROR");
        var owner = await s.Customers.GetAsync(identity, token) ?? throw Error("CUSTOMER_NOT_FOUND");
        Eligible(owner);
        var sender = await s.Wallets.FindOwnedAsync(owner.CustomerId, request.Currency, token) ?? throw Error("WALLET_NOT_FOUND");
        var target = await s.Beneficiaries.ReadAsync(owner.CustomerId, request.BeneficiaryId, token) ?? throw Error("BENEFICIARY_NOT_FOUND");
        var wallets = await s.Wallets.GetAsync(sender.WalletId, target.TargetWalletId, token);
        var recipient = ValidateWallets(wallets, sender.WalletId, target.TargetWalletId, owner.CustomerId, request.Currency);
        var beneficiary = await s.Beneficiaries.GetAsync(owner.CustomerId, request.BeneficiaryId, token) ?? throw Error("BENEFICIARY_NOT_FOUND");
        if (!beneficiary.IsActive || beneficiary.TargetWalletId != target.TargetWalletId) throw Error("BENEFICIARY_NOT_ACTIVE");
        var assessment = await s.Assessment.AssessLockedAsync(new(Guid.NewGuid(), new(owner.CustomerId, sender.WalletId, owner.Tier, request.AmountMinor, request.Currency, "Internal")), token);
        var balance = await s.Balances.GetWalletBalanceAsync(sender.WalletId, request.Currency, token) ?? throw Error("LEDGER_UNAVAILABLE");
        if (balance.AvailableBalance < assessment.TotalDebitMinor) throw Error("INSUFFICIENT_BALANCE");
        var now = clock.GetUtcNow();
        var quote = new TransferQuote
        {
            OwnerCustomerId = owner.CustomerId, SenderWalletId = sender.WalletId, BeneficiaryId = request.BeneficiaryId,
            TargetWalletId = beneficiary.TargetWalletId, TargetCustomerId = recipient.CustomerId, AmountMinor = request.AmountMinor,
            FeeMinor = assessment.FeeMinor, TotalDebitMinor = assessment.TotalDebitMinor, Currency = request.Currency,
            RiskDecision = assessment.RiskDecision.ToString(), ReasonCodesJson = JsonSerializer.Serialize(assessment.ReasonCodes),
            AssessmentJson = JsonSerializer.Serialize(assessment), FeeRuleVersionId = assessment.FeeRuleVersion.Id, FeeRuleVersion = assessment.FeeRuleVersion.Version,
            LimitRuleVersionsJson = JsonSerializer.Serialize(assessment.LimitRuleVersions), RiskRuleVersionsJson = JsonSerializer.Serialize(assessment.RiskRuleVersions),
            RequestFingerprint = Hash(new { owner.CustomerId, SenderWalletId = sender.WalletId, request.BeneficiaryId, beneficiary.TargetWalletId, request.AmountMinor, request.Currency }),
            MaskedSenderName = owner.MaskedName, MaskedRecipientName = beneficiary.MaskedRecipientName,
            CreatedAt = now, ExpiresAt = now.AddSeconds(settings.QuoteLifetimeSeconds)
        };
        s.Store.Add(quote); await s.Store.SaveAsync(token);
        return new QuoteResponse(quote.Id, quote.AmountMinor, quote.FeeMinor, quote.TotalDebitMinor, quote.Currency, quote.RiskDecision, assessment.ReasonCodes, quote.CreatedAt, quote.ExpiresAt);
    }, ct);

    public async Task<TransferReceipt> CreateAsync(Guid identity, string? key, CreateTransferRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) throw Error("IDEMPOTENCY_KEY_REQUIRED");
        if (key.Length > 128 || key.Any(c => c < '!' || c > '~') || request.QuoteId == Guid.Empty || request.Note?.Length > 500) throw Error("VALIDATION_ERROR");
        // Preserve exact note content in the fingerprint: changing even whitespace is a different request.
        var hash = Hash(request);
        var owner = await customers.GetByIdentityUserIdAsync(identity, ct) ?? throw Error("QUOTE_NOT_FOUND");
        var transfer = await coordinator.ExecuteAsync(async (s, token) =>
        {
            await s.Store.LockKeyAsync(owner.CustomerId, key, token);
            var replay = await s.Store.FindKeyAsync(owner.CustomerId, key, token);
            if (replay != null)
            {
                if (replay.RequestHash != hash) throw Error("IDEMPOTENCY_KEY_REUSED");
                return await s.Store.TransferAsync(identity, replay.TransferId, token) ?? throw Error("TRANSFER_NOT_FOUND");
            }
            var lockedOwner = await s.Customers.GetAsync(identity, token) ?? throw Error("QUOTE_NOT_FOUND");
            Eligible(lockedOwner);
            var quote = await s.Store.QuoteAsync(lockedOwner.CustomerId, request.QuoteId, token) ?? throw Error("QUOTE_NOT_FOUND");
            ValidateQuote(quote);
            if (await s.Store.HasIntentAsync(quote.Id, token)) throw Error("QUOTE_USED");
            var item = new Transfer
            {
                OwnerIdentityUserId = identity, OwnerCustomerId = quote.OwnerCustomerId, TargetCustomerId = quote.TargetCustomerId,
                SenderWalletId = quote.SenderWalletId, TargetWalletId = quote.TargetWalletId, BeneficiaryId = quote.BeneficiaryId,
                QuoteId = quote.Id, AmountMinor = quote.AmountMinor, FeeMinor = quote.FeeMinor, TotalDebitMinor = quote.TotalDebitMinor,
                Currency = quote.Currency, AssessmentJson = quote.AssessmentJson, MaskedSenderName = quote.MaskedSenderName,
                MaskedRecipientName = quote.MaskedRecipientName, Note = request.Note, CreatedAt = clock.GetUtcNow(), ExpiresAt = quote.ExpiresAt
            };
            s.Store.Add(item, new TransferIdempotency { OwnerCustomerId = owner.CustomerId, Key = key, RequestHash = hash,
                TransferId = item.Id, ResponseJson = JsonSerializer.Serialize(Receipt(item)), CreatedAt = item.CreatedAt });
            await s.Store.SaveAsync(token); return item;
        }, ct);
        if (transfer.Status == TransferStatus.PendingConfirmation && transfer.OtpChallengeId == null && clock.GetUtcNow() < transfer.ExpiresAt)
            await EnsureOtpAsync(transfer, ct);
        return Receipt(transfer);
    }

    public async Task<TransferReceipt> ResendAsync(Guid identity, Guid id, CancellationToken ct)
    {
        var item = await Owned(identity, id, ct);
        Pending(item);
        await EnsureOtpAsync(item, ct);
        return Receipt(await Owned(identity, id, ct));
    }

    private async Task EnsureOtpAsync(Transfer item, CancellationToken ct)
    {
        var issued = await otp.CreateAsync(new(item.OwnerIdentityUserId, item.Id, item.TargetWalletId,
            OtpChallengePurpose.TransferConfirmation, Details(item)), ct);
        await checkpoint.ReachedAsync("AfterOtpIssue", item.Id, ct);
        await coordinator.ExecuteAsync(async (s, token) =>
        {
            await s.Store.GetKeyAsync(item.Id, token);
            var current = await s.Store.TransferAsync(item.OwnerIdentityUserId, item.Id, token) ?? throw Error("TRANSFER_NOT_FOUND");
            // Identity serializes issuance. Its timestamp orders delayed responses so an older
            // challenge can never replace a newer one, while a newer response always repairs a stale reference.
            if (current.Status == TransferStatus.PendingConfirmation && current.OtpChallengeId != issued.ChallengeId &&
                (current.OtpIssuedAt == null || issued.CreatedAt > current.OtpIssuedAt))
            {
                current.OtpChallengeId = issued.ChallengeId; current.OtpIssuedAt = issued.CreatedAt;
                current.Touch(); await s.Store.SaveAsync(token);
            }
            return true;
        }, ct);
    }

    public Task<TransferReceipt> ConfirmAsync(Guid identity, Guid id, ConfirmTransferRequest request, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Transfers, OperationName.Confirm, () => ConfirmAsyncCore(identity, id, request, ct), r => r.Status == "Completed" ? OperationOutcome.Completed : OperationOutcome.Rejected);
    private async Task<TransferReceipt> ConfirmAsyncCore(Guid identity, Guid id, ConfirmTransferRequest request, CancellationToken ct)
    {
        var item = await Owned(identity, id, ct);
        if (Terminal(item)) return Receipt(item); // Durable receipt resolves a lost commit response.
        Pending(item);
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 32 || item.OtpChallengeId is null) throw Error("OTP_INVALID");
        var proof = new VerifyOtpChallenge(item.OtpChallengeId.Value, identity, item.Id, item.TargetWalletId,
            OtpChallengePurpose.TransferConfirmation, request.Code, Details(item));
        if (await otp.GetVerificationReceiptAsync(proof, ct) != OtpVerificationReceiptStatus.Verified)
        {
            var verified = await otp.VerifyAsync(proof, ct);
            if (verified != OtpVerificationStatus.Verified && await otp.GetVerificationReceiptAsync(proof, ct) != OtpVerificationReceiptStatus.Verified)
                throw Error(verified switch { OtpVerificationStatus.Expired => "OTP_EXPIRED", OtpVerificationStatus.AttemptsExceeded => "OTP_ATTEMPTS_EXCEEDED", _ => "OTP_INVALID" });
        }
        await checkpoint.ReachedAsync("AfterOtp", id, ct);
        return await coordinator.ExecuteAsync(async (s, token) =>
        {
            var key = await s.Store.GetKeyAsync(id, token);
            var current = await s.Store.TransferAsync(identity, id, token) ?? throw Error("TRANSFER_NOT_FOUND");
            if (Terminal(current)) return Receipt(current);
            Pending(current);
            if (current.OtpChallengeId != proof.ChallengeId || Details(current) != proof.Transfer || current.TargetWalletId != proof.TargetWalletId)
                throw Error("STATE_CONFLICT");
            var owner = await s.Customers.GetAsync(identity, token) ?? throw Error("TRANSFER_NOT_FOUND");
            Eligible(owner);
            if (owner.CustomerId != current.OwnerCustomerId) throw Error("TRANSFER_NOT_FOUND");
            var wallets = await s.Wallets.GetAsync(current.SenderWalletId, current.TargetWalletId, token);
            var recipient = ValidateWallets(wallets, current.SenderWalletId, current.TargetWalletId, owner.CustomerId, current.Currency);
            if (recipient.CustomerId != current.TargetCustomerId) throw Error("STATE_CONFLICT");
            var beneficiary = await s.Beneficiaries.GetAsync(owner.CustomerId, current.BeneficiaryId, token) ?? throw Error("BENEFICIARY_NOT_ACTIVE");
            if (!beneficiary.IsActive || beneficiary.TargetWalletId != current.TargetWalletId) throw Error("BENEFICIARY_NOT_ACTIVE");
            var quote = await s.Store.QuoteAsync(owner.CustomerId, current.QuoteId, token) ?? throw Error("QUOTE_NOT_FOUND");
            ValidateQuote(quote);
            if (quote.SenderWalletId != current.SenderWalletId || quote.TargetWalletId != current.TargetWalletId || quote.BeneficiaryId != current.BeneficiaryId ||
                quote.AmountMinor != current.AmountMinor || quote.FeeMinor != current.FeeMinor || quote.Currency != current.Currency || quote.TotalDebitMinor != current.TotalDebitMinor)
                throw Error("STATE_CONFLICT");
            var consumption = new ConsumeLimitUsage(current.Id, new(owner.CustomerId, current.SenderWalletId, owner.Tier, current.AmountMinor, current.Currency, "Internal"));
            var assessment = await s.Assessment.AssessLockedAsync(consumption, token);
            MatchQuote(quote, assessment);
            current.BeginValidation(clock.GetUtcNow());
            if (assessment.RiskDecision != RiskDecision.Allow)
            {
                current.Finish(assessment.RiskDecision == RiskDecision.Review ? TransferStatus.PendingReview : TransferStatus.Rejected,
                    assessment.ReasonCodes.FirstOrDefault(x => x != "RISK_ALLOWED"));
            }
            else
            {
                var accounts = await s.Ledger.LockAsync(current.Id, current.SenderWalletId, current.TargetWalletId, current.Currency, current.FeeMinor > 0, token);
                if (accounts.AvailableBalance < current.TotalDebitMinor) throw Error("INSUFFICIENT_BALANCE");
                var usage = await s.Usage.ConsumeAsync(consumption, token);
                MatchQuote(quote, usage.Assessment);
                if (!usage.Consumed) throw Error("QUOTE_STALE");
                await checkpoint.ReachedAsync("AfterUsage", id, token);
                var lines = new List<Posting> { new(accounts.SenderAccountId, PostingSide.Debit, current.TotalDebitMinor), new(accounts.RecipientAccountId, PostingSide.Credit, current.AmountMinor) };
                if (current.FeeMinor > 0) lines.Add(new(accounts.FeeAccountId, PostingSide.Credit, current.FeeMinor));
                var journal = await s.Posting.PostAsync(new("InternalTransfer", id.ToString("D"), current.Currency, lines, "Internal transfer", identity), token);
                await checkpoint.ReachedAsync("AfterLedger", id, token);
                var now = clock.GetUtcNow();
                // Time may advance while waiting on locks; never consume a quote that expired during posting.
                if (now >= quote.ExpiresAt) throw Error("QUOTE_EXPIRED");
                quote.Use(now);
                current.Finish(TransferStatus.Completed, journal: journal.Id, at: now);
            }
            var receipt = Receipt(current);
            key.ResponseJson = JsonSerializer.Serialize(receipt);
            await s.Store.SaveAsync(token);
            await checkpoint.ReachedAsync("BeforeCommit", id, token);
            return receipt;
        }, ct);
    }

    public async Task<TransferReceipt> GetAsync(Guid identityUserId, Guid id, bool canViewAny, CancellationToken ct)
    {
        var customer = canViewAny ? null : await customers.GetByIdentityUserIdAsync(identityUserId, ct);
        return await queries.GetAsync(customer?.CustomerId, id, canViewAny, ct) ?? throw Error("TRANSFER_NOT_FOUND");
    }
    public async Task<TransferPage> ListAsync(Guid identityUserId, bool canViewAny, string? cursor, int? pageSize, CancellationToken ct)
    {
        var customer = canViewAny ? null : await customers.GetByIdentityUserIdAsync(identityUserId, ct);
        return await queries.ListAsync(customer?.CustomerId, canViewAny, cursor, Math.Clamp(pageSize ?? 20, 1, settings.MaxPageSize), ct);
    }
    private async Task<Transfer> Owned(Guid identity, Guid id, CancellationToken ct) => await queries.OwnedAsync(identity, id, ct) ?? throw Error("TRANSFER_NOT_FOUND");
    private void Pending(Transfer t)
    {
        if (t.Status != TransferStatus.PendingConfirmation) throw Error("STATE_CONFLICT");
        if (clock.GetUtcNow() >= t.ExpiresAt) throw Error("TRANSFER_EXPIRED");
    }
    private void ValidateQuote(TransferQuote q)
    {
        if (q.UsedAt != null) throw Error("QUOTE_USED");
        if (clock.GetUtcNow() >= q.ExpiresAt) throw Error("QUOTE_EXPIRED");
    }
    private static void MatchQuote(TransferQuote q, TransferAssessment a)
    {
        var prior = JsonSerializer.Deserialize<TransferAssessment>(q.AssessmentJson)!;
        if (a.FeeMinor != q.FeeMinor || a.TotalDebitMinor != q.TotalDebitMinor || a.RiskDecision.ToString() != q.RiskDecision ||
            a.FeeRuleVersion != prior.FeeRuleVersion || !a.LimitRuleVersions.OrderBy(x => x.Id).SequenceEqual(prior.LimitRuleVersions.OrderBy(x => x.Id)) ||
            !a.RiskRuleVersions.OrderBy(x => x.Id).SequenceEqual(prior.RiskRuleVersions.OrderBy(x => x.Id))) throw Error("QUOTE_STALE");
    }
    private static TransferWallet ValidateWallets(IReadOnlyList<TransferWallet> wallets, Guid source, Guid target, Guid owner, string currency)
    {
        if (source == target) throw Error("SELF_TRANSFER_NOT_ALLOWED");
        var sender = wallets.SingleOrDefault(x => x.WalletId == source);
        var recipient = wallets.SingleOrDefault(x => x.WalletId == target);
        if (sender is null || sender.CustomerId != owner) throw Error("WALLET_NOT_FOUND");
        if (recipient is null || !sender.IsActive || !recipient.IsActive) throw Error("WALLET_NOT_ACTIVE");
        if (recipient.CustomerId == owner) throw Error("SELF_TRANSFER_NOT_ALLOWED");
        if (sender.Currency != currency || recipient.Currency != currency) throw Error("VALIDATION_ERROR");
        return recipient;
    }
    private static void Eligible(TransferCustomer owner) { if (!owner.IsEligible) throw Error("CUSTOMER_NOT_ACTIVE"); }
    private static bool Terminal(Transfer t) => t.Status is TransferStatus.Completed or TransferStatus.PendingReview or TransferStatus.Rejected or TransferStatus.Failed;
    private static TransferOtpDetails Details(Transfer t) => new(t.SenderWalletId, t.AmountMinor, t.FeeMinor, t.Currency);
    public static TransferReceipt Receipt(Transfer t) => new(t.Id, t.PublicReference, t.Status.ToString(), t.AmountMinor, t.FeeMinor, t.TotalDebitMinor, t.Currency,
        t.MaskedSenderName, t.MaskedRecipientName, t.CreatedAt, t.ExpiresAt, t.CompletedAt, t.ReasonCode);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static TransferException Error(string code) => new(code);
}
