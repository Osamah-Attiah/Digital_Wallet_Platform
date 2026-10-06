using DigitalWallet.RiskLimits.Domain;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.RiskLimits.Infrastructure;

public sealed class RiskLimitsDbContext(DbContextOptions<RiskLimitsDbContext> options) : DbContext(options)
{
    public const string Schema = "risk_limits";
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<PeriodUsage> Usage => Set<PeriodUsage>();
    public DbSet<UsageOperation> Operations => Set<UsageOperation>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Rule>(e =>
        {
            e.ToTable("rules", t =>
            {
                t.HasCheckConstraint("ck_rule_identity", "\"Kind\" BETWEEN 0 AND 2 AND \"Scope\" BETWEEN 0 AND 3 AND \"Version\" > 0 AND \"Currency\" = 'SAR' AND \"TransferType\" = 'Internal' AND length(btrim(\"Reason\")) > 0 AND \"CreatedBy\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_rule_scope", "(\"Scope\" = 0 AND \"ScopeKey\" = '*') OR (\"Scope\" = 1 AND \"ScopeKey\" ~ '^[A-Za-z0-9-]{1,64}$') OR (\"Scope\" IN (2,3) AND \"ScopeKey\" ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' AND \"ScopeKey\" <> '00000000-0000-0000-0000-000000000000')");
                t.HasCheckConstraint("ck_rule_dates", "(\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\") AND ((\"PublishedAt\" IS NULL AND \"PublishedBy\" IS NULL) OR (\"PublishedAt\" IS NOT NULL AND \"PublishedBy\" IS NOT NULL AND \"PublishedBy\" <> '00000000-0000-0000-0000-000000000000'::uuid))");
                t.HasCheckConstraint("ck_rule_values", """
                    ("MinimumTransferMinor" IS NULL OR "MinimumTransferMinor" >= 0) AND
                    ("MaximumTransferMinor" IS NULL OR "MaximumTransferMinor" >= 0) AND
                    ("DailyAmountMinor" IS NULL OR "DailyAmountMinor" >= 0) AND ("DailyCount" IS NULL OR "DailyCount" >= 0) AND
                    ("MonthlyAmountMinor" IS NULL OR "MonthlyAmountMinor" >= 0) AND ("MonthlyCount" IS NULL OR "MonthlyCount" >= 0) AND
                    ("FixedFeeMinor" IS NULL OR "FixedFeeMinor" >= 0) AND ("BasisPoints" IS NULL OR "BasisPoints" BETWEEN 0 AND 10000) AND
                    ("MinimumFeeMinor" IS NULL OR "MinimumFeeMinor" >= 0) AND ("MaximumFeeMinor" IS NULL OR "MaximumFeeMinor" >= 0) AND
                    ("ReviewFromMinor" IS NULL OR "ReviewFromMinor" >= 0) AND ("DenyFromMinor" IS NULL OR "DenyFromMinor" >= 0) AND
                    ("MinimumTransferMinor" IS NULL OR "MaximumTransferMinor" IS NULL OR "MinimumTransferMinor" <= "MaximumTransferMinor") AND
                    ("MinimumFeeMinor" IS NULL OR "MaximumFeeMinor" IS NULL OR "MinimumFeeMinor" <= "MaximumFeeMinor") AND
                    ("ReviewFromMinor" IS NULL OR "DenyFromMinor" IS NULL OR "ReviewFromMinor" <= "DenyFromMinor")
                    """);
                t.HasCheckConstraint("ck_rule_payload", """
                    ("Kind" = 0 OR ("MinimumTransferMinor" IS NULL AND "MaximumTransferMinor" IS NULL AND "DailyAmountMinor" IS NULL AND "DailyCount" IS NULL AND "MonthlyAmountMinor" IS NULL AND "MonthlyCount" IS NULL)) AND
                    ("Kind" = 1 OR ("FixedFeeMinor" IS NULL AND "BasisPoints" IS NULL AND "MinimumFeeMinor" IS NULL AND "MaximumFeeMinor" IS NULL)) AND
                    ("Kind" = 2 OR ("ReviewFromMinor" IS NULL AND "DenyFromMinor" IS NULL)) AND
                    ("Kind" <> 0 OR COALESCE("MinimumTransferMinor", "MaximumTransferMinor", "DailyAmountMinor", "DailyCount", "MonthlyAmountMinor", "MonthlyCount") IS NOT NULL) AND
                    ("Kind" <> 1 OR ("FixedFeeMinor" IS NOT NULL AND "BasisPoints" IS NOT NULL))
                    """);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.ScopeKey).HasMaxLength(64);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.TransferType).HasMaxLength(32);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => new { x.Kind, x.Scope, x.ScopeKey, x.Currency, x.TransferType, x.Version }).IsUnique();
        });
        model.Entity<PeriodUsage>(e =>
        {
            e.ToTable("period_usage", t => t.HasCheckConstraint("ck_usage", "\"AmountMinor\" >= 0 AND \"Count\" >= 0 AND \"Period\" IN ('Day','Month') AND \"PeriodEnd\" > \"PeriodStart\""));
            e.HasKey(x => new { x.Subject, x.RuleVersionId, x.Period, x.PeriodStart });
            e.Property(x => x.Subject).HasMaxLength(48);
            e.Property(x => x.Period).HasMaxLength(5);
            e.HasOne<Rule>().WithMany().HasForeignKey(x => x.RuleVersionId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<UsageOperation>(e =>
        {
            e.ToTable("usage_operations", t => t.HasCheckConstraint("ck_operation_amount", "\"AmountMinor\" > 0")); e.HasKey(x => x.OperationId);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.TransferType).HasMaxLength(32);
            e.HasIndex(x => new { x.CustomerId, x.Currency, x.TransferType, x.EvaluatedAt }).HasFilter("\"Consumed\"");
            e.HasIndex(x => new { x.WalletId, x.Currency, x.TransferType, x.EvaluatedAt }).HasFilter("\"Consumed\"");
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
        });
    }
}
