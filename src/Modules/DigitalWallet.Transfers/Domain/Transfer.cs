namespace DigitalWallet.Transfers.Domain;

public enum TransferStatus { PendingConfirmation, Validating, PendingReview, Completed, Rejected, Failed }

public sealed class TransferQuote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerCustomerId { get; set; }
    public Guid SenderWalletId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public Guid TargetWalletId { get; set; }
    public Guid TargetCustomerId { get; set; }
    public long AmountMinor { get; set; }
    public long FeeMinor { get; set; }
    public long TotalDebitMinor { get; set; }
    public string Currency { get; set; } = "SAR";
    public string RiskDecision { get; set; } = "";
    public string ReasonCodesJson { get; set; } = "[]";
    public string AssessmentJson { get; set; } = "";
    public Guid FeeRuleVersionId { get; set; }
    public int FeeRuleVersion { get; set; }
    public string LimitRuleVersionsJson { get; set; } = "[]";
    public string RiskRuleVersionsJson { get; set; } = "[]";
    public string RequestFingerprint { get; set; } = "";
    public string MaskedSenderName { get; set; } = "";
    public string MaskedRecipientName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public long Version { get; set; } = 1;
    public Guid ConcurrencyVersion { get; set; } = Guid.NewGuid();
    public void Use(DateTimeOffset now)
    {
        if (UsedAt != null || now >= ExpiresAt) throw new InvalidOperationException("Quote is unavailable.");
        UsedAt = now; Version++; ConcurrencyVersion = Guid.NewGuid();
    }
}

public sealed class Transfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PublicReference { get; set; } = "TRF-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
    public Guid OwnerIdentityUserId { get; set; }
    public Guid OwnerCustomerId { get; set; }
    public Guid TargetCustomerId { get; set; }
    public Guid SenderWalletId { get; set; }
    public Guid TargetWalletId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public Guid QuoteId { get; set; }
    public long AmountMinor { get; set; }
    public long FeeMinor { get; set; }
    public long TotalDebitMinor { get; set; }
    public string Currency { get; set; } = "SAR";
    public string AssessmentJson { get; set; } = "";
    public string MaskedSenderName { get; set; } = "";
    public string MaskedRecipientName { get; set; } = "";
    public string? Note { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.PendingConfirmation;
    public string? ReasonCode { get; set; }
    public Guid? OtpChallengeId { get; set; }
    public DateTimeOffset? OtpIssuedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public Guid? JournalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; } = 1;
    public Guid ConcurrencyVersion { get; set; } = Guid.NewGuid();
    public void Touch() { Version++; ConcurrencyVersion = Guid.NewGuid(); }
    public void BeginValidation(DateTimeOffset at)
    {
        if (Status != TransferStatus.PendingConfirmation) throw new InvalidOperationException("Transfer is not pending.");
        Status = TransferStatus.Validating; ConfirmedAt = at; Touch();
    }
    public void Finish(TransferStatus status, string? reason = null, Guid? journal = null, DateTimeOffset? at = null)
    {
        if (Status != TransferStatus.Validating || status is not (TransferStatus.Completed or TransferStatus.PendingReview or TransferStatus.Rejected))
            throw new InvalidOperationException("Invalid transfer transition.");
        if (status == TransferStatus.Completed && (journal is null || at is null)) throw new InvalidOperationException("Completion requires posting evidence.");
        Status = status; ReasonCode = reason; JournalId = journal; CompletedAt = at; Touch();
    }
}

public sealed class TransferIdempotency
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerCustomerId { get; set; }
    public string Scope { get; set; } = "transfer.create";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid TransferId { get; set; }
    public string ResponseJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
