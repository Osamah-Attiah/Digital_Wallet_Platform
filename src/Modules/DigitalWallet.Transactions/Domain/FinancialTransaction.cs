namespace DigitalWallet.Transactions.Domain;

// Append-only facts. Reversed status is derived from a separate compensating fact.
public sealed class FinancialTransaction
{
    public Guid Id { get; set; }
    public string PublicReference { get; set; } = "";
    public string Type { get; set; } = "InternalTransfer";
    public Guid TransferId { get; set; }
    public Guid JournalId { get; set; }
    public Guid SenderCustomerId { get; set; }
    public Guid RecipientCustomerId { get; set; }
    public Guid SenderWalletId { get; set; }
    public Guid RecipientWalletId { get; set; }
    public long AmountMinor { get; set; }
    public long FeeMinor { get; set; }
    public long SenderDebitMinor { get; set; }
    public long RecipientCreditMinor { get; set; }
    public string Currency { get; set; } = "SAR";
    public string MaskedSenderName { get; set; } = "";
    public string MaskedRecipientName { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ReversalOf { get; set; }
}

public sealed class ReversalRequest
{
    public Guid Id { get; set; }
    public Guid OriginalTransactionId { get; set; }
    public Guid RequestedBy { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public string? ApprovalKey { get; set; }
    public string? ApprovalHash { get; set; }
    public Guid? OtpChallengeId { get; set; }
    public Guid? ReversalTransactionId { get; set; }
}
