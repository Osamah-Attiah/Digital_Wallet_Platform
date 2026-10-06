using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.RiskLimits.Domain;

namespace DigitalWallet.RiskLimits.Application;

public sealed record CreateRule(RuleKind Kind, RuleScope Scope, string ScopeKey, int Version,
    string Currency, string TransferType, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo, string Reason,
    long? MinimumTransferMinor = null, long? MaximumTransferMinor = null,
    long? DailyAmountMinor = null, long? DailyCount = null, long? MonthlyAmountMinor = null, long? MonthlyCount = null,
    long? FixedFeeMinor = null, int? BasisPoints = null, long? MinimumFeeMinor = null, long? MaximumFeeMinor = null,
    long? ReviewFromMinor = null, long? DenyFromMinor = null);
public sealed record RulePage(IReadOnlyList<Rule> Items, int? NextAfterVersion);
public interface IRuleAdministration
{
    Task<Rule> CreateAsync(CreateRule request, Guid actor, CancellationToken ct = default);
    Task<Rule> PublishAsync(Guid id, Guid actor, CancellationToken ct = default);
    Task<Rule> GetAsync(Guid id, CancellationToken ct = default);
    Task<RulePage> ListAsync(RuleKind kind, RuleScope scope, string key, string currency, string transferType,
        int afterVersion, int pageSize, CancellationToken ct = default);
}
public static class RuleValidation
{
    public static void Validate(CreateRule r)
    {
        var limits = new[] { r.MinimumTransferMinor, r.MaximumTransferMinor, r.DailyAmountMinor, r.DailyCount, r.MonthlyAmountMinor, r.MonthlyCount };
        var fees = new[] { r.FixedFeeMinor, r.MinimumFeeMinor, r.MaximumFeeMinor };
        var risks = new[] { r.ReviewFromMinor, r.DenyFromMinor };
        if (!Enum.IsDefined(r.Kind) || !ValidKey(r.Scope, r.ScopeKey) || r.Version < 1 ||
            r.Currency != "SAR" || r.TransferType != "Internal" || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 500 ||
            r.EffectiveFrom.Offset != TimeSpan.Zero || (r.EffectiveTo != null && r.EffectiveTo.Value.Offset != TimeSpan.Zero) ||
            r.EffectiveTo <= r.EffectiveFrom || r.EffectiveFrom == default ||
            limits.Concat(fees).Concat(risks).Any(x => x < 0) || r.BasisPoints is < 0 or > 10000 ||
            r.MinimumTransferMinor > r.MaximumTransferMinor || r.MinimumFeeMinor > r.MaximumFeeMinor ||
            r.ReviewFromMinor > r.DenyFromMinor ||
            (r.Kind != RuleKind.Limit && limits.Any(x => x != null)) ||
            (r.Kind != RuleKind.Fee && (fees.Any(x => x != null) || r.BasisPoints != null)) ||
            (r.Kind != RuleKind.Risk && risks.Any(x => x != null)) ||
            (r.Kind == RuleKind.Limit && limits.All(x => x == null)) ||
            (r.Kind == RuleKind.Fee && (r.FixedFeeMinor == null || r.BasisPoints == null)))
            throw new RiskLimitsException("VALIDATION_ERROR", "Invalid rule definition.");
    }
    public static bool ValidKey(RuleScope scope, string? key) => scope switch
    {
        RuleScope.System => key == "*",
        RuleScope.CustomerTier => !string.IsNullOrWhiteSpace(key) && key.Length <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'),
        RuleScope.Customer or RuleScope.Wallet => Guid.TryParseExact(key, "D", out var id) && id != Guid.Empty && key == id.ToString("D"),
        _ => false
    };
}
