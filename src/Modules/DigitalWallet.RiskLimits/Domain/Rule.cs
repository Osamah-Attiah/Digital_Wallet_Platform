namespace DigitalWallet.RiskLimits.Domain;

public enum RuleKind { Limit, Fee, Risk }
public enum RuleScope { System, CustomerTier, Customer, Wallet }

// A row is one version. Once published PostgreSQL prohibits every UPDATE/DELETE.
public sealed class Rule
{
    public Guid Id { get; set; }
    public RuleKind Kind { get; set; }
    public RuleScope Scope { get; set; }
    public string ScopeKey { get; set; } = "*";
    public int Version { get; set; }
    public string Currency { get; set; } = "SAR";
    public string TransferType { get; set; } = "Internal";
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? PublishedBy { get; set; }
    public string Reason { get; set; } = "";
    public long? MinimumTransferMinor { get; set; }
    public long? MaximumTransferMinor { get; set; }
    public long? DailyAmountMinor { get; set; }
    public long? DailyCount { get; set; }
    public long? MonthlyAmountMinor { get; set; }
    public long? MonthlyCount { get; set; }
    public long? FixedFeeMinor { get; set; }
    public int? BasisPoints { get; set; }
    public long? MinimumFeeMinor { get; set; }
    public long? MaximumFeeMinor { get; set; }
    public long? ReviewFromMinor { get; set; }
    public long? DenyFromMinor { get; set; }
}
public sealed class PeriodUsage
{
    public string Subject { get; set; } = "";
    public Guid RuleVersionId { get; set; }
    public string Period { get; set; } = "";
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public long AmountMinor { get; set; }
    public long Count { get; set; }
}
public sealed class UsageOperation
{
    public Guid OperationId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid WalletId { get; set; }
    public string Currency { get; set; } = "";
    public string TransferType { get; set; } = "";
    public DateTimeOffset EvaluatedAt { get; set; }
    public long AmountMinor { get; set; }
    public bool Consumed { get; set; }
    public string RequestHash { get; set; } = "";
    public string ResultJson { get; set; } = "";
}
public sealed record BusinessPeriod(string Kind, DateTimeOffset Start, DateTimeOffset End);
public static class RiyadhPeriods
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    public static BusinessPeriod[] At(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, Zone).Date;
        var month = new DateTime(local.Year, local.Month, 1);
        static DateTimeOffset Utc(DateTime date) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), Zone));
        return [new("Day", Utc(local), Utc(local.AddDays(1))), new("Month", Utc(month), Utc(month.AddMonths(1)))];
    }
}
