namespace DigitalWallet.Ledger.Domain;

public enum AccountKind { CustomerWalletLiability, AdjustmentControl, PlatformFeeRevenue, Suspense }
public enum EntrySide { Debit, Credit }
public enum JournalStatus { Draft, Posted }

public sealed class LedgerAccount
{
    public Guid Id { get; init; }
    public Guid? WalletId { get; init; }
    public required string Code { get; init; }
    public required string Currency { get; init; }
    public AccountKind Kind { get; init; }
    public EntrySide NormalSide { get; init; }
}

public sealed class JournalEntry
{
    public Guid Id { get; init; }
    public required string SourceType { get; init; }
    public required string SourceId { get; init; }
    public required string RequestHash { get; init; }
    public required string Currency { get; init; }
    public required string Reason { get; init; }
    public Guid? ActorId { get; init; }
    public Guid? ReversalOf { get; init; }
    public JournalStatus Status { get; private set; }
    public DateTimeOffset? PostedAt { get; private set; }
}

public sealed class LedgerEntry
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid AccountId { get; init; }
    public EntrySide Side { get; init; }
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }
}

public sealed class AccountBalance
{
    public Guid AccountId { get; init; }
    public long BalanceMinor { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}

public static class LedgerRules
{
    // Deliberate supported ISO-4217 subset. Wallet product remains SAR-only; no FX.
    public static readonly IReadOnlySet<string> Currencies = new HashSet<string>(StringComparer.Ordinal)
        { "SAR", "USD", "EUR", "GBP", "JPY", "AED", "KWD", "BHD", "QAR", "OMR" };

    public static bool IsBalanced(IEnumerable<(EntrySide Side, long Amount)> entries)
    {
        decimal debit = 0, credit = 0;
        var count = 0;
        foreach (var (side, amount) in entries)
        {
            if (amount <= 0 || !Enum.IsDefined(side)) return false;
            if (side == EntrySide.Debit) debit += amount; else credit += amount;
            count++;
        }
        return count >= 2 && debit == credit;
    }
}
