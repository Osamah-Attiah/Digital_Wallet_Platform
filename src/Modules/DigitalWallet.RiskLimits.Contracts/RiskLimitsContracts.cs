namespace DigitalWallet.RiskLimits.Contracts;

public enum RiskDecision { Allow, Review, Deny }
public sealed record RuleVersion(Guid Id, int Version);
public sealed record EffectiveLimits(long? MinimumTransferMinor, long? MaximumTransferMinor,
    long? DailyAmountMinor, long? DailyCount, long? MonthlyAmountMinor, long? MonthlyCount);
// Trusted server-side input. Transfers must resolve ownership, tier and eligibility via module contracts.
// No fee, timestamps or rule versions can be supplied by the customer.
public sealed record AssessTransfer(Guid CustomerId, Guid WalletId, string CustomerTier,
    long AmountMinor, string Currency, string TransferType);
public sealed record TransferAssessment(long AmountMinor, long FeeMinor, long TotalDebitMinor,
    string Currency, EffectiveLimits EffectiveLimits, RuleVersion FeeRuleVersion,
    IReadOnlyList<RuleVersion> LimitRuleVersions, RiskDecision RiskDecision,
    IReadOnlyList<string> ReasonCodes, IReadOnlyList<RuleVersion> RiskRuleVersions, DateTimeOffset EvaluatedAt);
public interface ITransferAssessment
{
    Task<TransferAssessment> AssessAsync(AssessTransfer request, CancellationToken ct = default);
}
public sealed record ConsumeLimitUsage(Guid OperationId, AssessTransfer Transfer);
public sealed record LimitConsumption(Guid OperationId, bool Consumed, TransferAssessment Assessment);
// Internal server contract; no HTTP route consumes usage. Only Allow can consume.
public interface ILimitUsage
{
    Task<LimitConsumption> ConsumeAsync(ConsumeLimitUsage request, CancellationToken ct = default);
}
public sealed class RiskLimitsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface ILockedTransferAssessment
{
    // Acquires the same configuration/operation/subject locks as consumption, without reserving usage.
    Task<TransferAssessment> AssessLockedAsync(ConsumeLimitUsage request, CancellationToken ct = default);
}
