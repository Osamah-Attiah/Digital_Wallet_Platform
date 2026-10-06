using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.RiskLimits.Domain;

namespace DigitalWallet.RiskLimits.Application;

public static class AssessmentEngine
{
    public static void Validate(AssessTransfer r)
    {
        if (r.CustomerId == Guid.Empty || r.WalletId == Guid.Empty || !RuleValidation.ValidKey(RuleScope.CustomerTier, r.CustomerTier) ||
            r.AmountMinor <= 0 || r.Currency != "SAR" || r.TransferType != "Internal")
            throw new RiskLimitsException("VALIDATION_ERROR", "Invalid transfer assessment input.");
    }
    public static string Subject(Rule rule, AssessTransfer r) => rule.Scope == RuleScope.Wallet ? $"wallet:{r.WalletId:D}" : $"customer:{r.CustomerId:D}";
    public static TransferAssessment Evaluate(AssessTransfer request, IReadOnlyList<Rule> rules,
        IReadOnlyList<PeriodUsage> usages, DateTimeOffset at)
    {
        Validate(request);
        // Require explicit system baselines: absence of configuration never silently removes protection or fees.
        if (Enum.GetValues<RuleKind>().Any(k => !rules.Any(x => x.Kind == k && x.Scope == RuleScope.System)))
            throw new RiskLimitsException("RULES_NOT_CONFIGURED", "Applicable rules are unavailable.");
        var limits = rules.Where(x => x.Kind == RuleKind.Limit).OrderBy(x => x.Id).ToArray();
        var fee = rules.Where(x => x.Kind == RuleKind.Fee).OrderByDescending(x => x.Scope).First();
        var risk = rules.Where(x => x.Kind == RuleKind.Risk).OrderBy(x => x.Id).ToArray();
        static long? Min(IEnumerable<long?> values) => values.Min();
        var effective = new EffectiveLimits(limits.Max(x => x.MinimumTransferMinor), Min(limits.Select(x => x.MaximumTransferMinor)),
            Min(limits.Select(x => x.DailyAmountMinor)), Min(limits.Select(x => x.DailyCount)),
            Min(limits.Select(x => x.MonthlyAmountMinor)), Min(limits.Select(x => x.MonthlyCount)));
        var feeMinor = CalculateFee(request.AmountMinor, fee);
        var total = (decimal)request.AmountMinor + feeMinor;
        if (total > long.MaxValue) throw new RiskLimitsException("AMOUNT_OUT_OF_RANGE", "Amount cannot be represented.");
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var decision = RiskDecision.Allow;
        if (request.AmountMinor < effective.MinimumTransferMinor || request.AmountMinor > effective.MaximumTransferMinor)
            reasons.Add("TRANSFER_AMOUNT_LIMIT_EXCEEDED");
        foreach (var rule in limits)
        foreach (var period in RiyadhPeriods.At(at))
        {
            var usage = usages.SingleOrDefault(x => x.RuleVersionId == rule.Id && x.Subject == Subject(rule, request) && x.Period == period.Kind && x.PeriodStart == period.Start);
            var amountLimit = period.Kind == "Day" ? rule.DailyAmountMinor : rule.MonthlyAmountMinor;
            var countLimit = period.Kind == "Day" ? rule.DailyCount : rule.MonthlyCount;
            var amount = (decimal)(usage?.AmountMinor ?? 0) + request.AmountMinor;
            var count = (decimal)(usage?.Count ?? 0) + 1;
            if (amount > amountLimit || count > countLimit)
                reasons.Add(period.Kind == "Day" ? "DAILY_LIMIT_EXCEEDED" : "MONTHLY_LIMIT_EXCEEDED");
            if (amount > long.MaxValue || count > long.MaxValue)
                reasons.Add("AMOUNT_OUT_OF_RANGE");
        }
        if (reasons.Count > 0) decision = RiskDecision.Deny;
        foreach (var rule in risk)
        {
            if (request.AmountMinor >= rule.DenyFromMinor) { decision = RiskDecision.Deny; reasons.Add("RISK_REJECTED"); }
            else if (request.AmountMinor >= rule.ReviewFromMinor)
            { if (decision < RiskDecision.Review) decision = RiskDecision.Review; reasons.Add("RISK_REVIEW_REQUIRED"); }
        }
        if (decision == RiskDecision.Allow) reasons.Add("RISK_ALLOWED");
        return new(request.AmountMinor, feeMinor, (long)total, request.Currency, effective, new(fee.Id, fee.Version),
            limits.Select(x => new RuleVersion(x.Id, x.Version)).ToArray(), decision, reasons.ToArray(),
            risk.Select(x => new RuleVersion(x.Id, x.Version)).ToArray(), at);
    }
    public static long CalculateFee(long amount, Rule rule)
    {
        // Round once to nearest minor unit, midpoint away from zero; all inputs are nonnegative.
        var value = decimal.Round((decimal)amount * rule.BasisPoints!.Value / 10000m + rule.FixedFeeMinor!.Value, 0, MidpointRounding.AwayFromZero);
        value = Math.Max(value, rule.MinimumFeeMinor ?? 0);
        if (rule.MaximumFeeMinor is { } maximum) value = Math.Min(value, maximum);
        if (value > long.MaxValue) throw new RiskLimitsException("AMOUNT_OUT_OF_RANGE", "Fee cannot be represented.");
        return (long)value;
    }
}
