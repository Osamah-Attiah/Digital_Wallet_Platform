using System.Data;
using Rule = DigitalWallet.RiskLimits.Domain.Rule;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalWallet.RiskLimits.Application;
using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.RiskLimits.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DigitalWallet.RiskLimits.Infrastructure;

public sealed class RiskLimitsStore(RiskLimitsDbContext db, TimeProvider clock, IOptions<RiskLimitsOptions> options)
    : ITransferAssessment, ILimitUsage, IRuleAdministration, ILockedTransferAssessment
{
    // Shared by assessment participants; publication takes the exclusive form until commit.
    private const long ConfigurationLock = 610060006;
    public async Task<TransferAssessment> AssessAsync(AssessTransfer request, CancellationToken ct = default)
    {
        AssessmentEngine.Validate(request);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var at = clock.GetUtcNow();
        var rules = await Matching(request, at, ct);
        var usage = await ReadUsage(request, rules, at, ct);
        var result = AssessmentEngine.Evaluate(request, rules, usage, at);
        await tx.CommitAsync(ct);
        return result;
    }
    public Task<TransferAssessment> AssessLockedAsync(ConsumeLimitUsage request, CancellationToken ct = default) =>
        DigitalWallet.SharedKernel.Transactions.ParticipantSavepoint.RunAsync(
            Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(db.Database.CurrentTransaction
                ?? throw new InvalidOperationException("A coordinator transaction is required.")), async () =>
        {
            AssessmentEngine.Validate(request.Transfer);
            if (request.OperationId == Guid.Empty) throw new RiskLimitsException("VALIDATION_ERROR", "Operation ID is required.");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared({ConfigurationLock})", ct);
            await Lock("operation:" + request.OperationId.ToString("D"), ct);
            foreach (var subject in new[] { $"customer:{request.Transfer.CustomerId:D}", $"wallet:{request.Transfer.WalletId:D}" }.Order(StringComparer.Ordinal))
                await Lock(subject, ct);
            var at = clock.GetUtcNow();
            var rules = await Matching(request.Transfer, at, ct);
            return AssessmentEngine.Evaluate(request.Transfer, rules, await ReadUsage(request.Transfer, rules, at, ct), at);
        }, ct);

    public async Task<LimitConsumption> ConsumeAsync(ConsumeLimitUsage request, CancellationToken ct = default)
    {
        AssessmentEngine.Validate(request.Transfer);
        if (request.OperationId == Guid.Empty) throw new RiskLimitsException("VALIDATION_ERROR", "Operation ID is required.");
        var owned = db.Database.CurrentTransaction is null;
        var tx = db.Database.CurrentTransaction ?? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // A local savepoint ensures that a failed participant leaves no usage even if its caller catches the error.
        var savepoint = "risk_" + Guid.NewGuid().ToString("N");
        var hasSavepoint = false;
        try
        {
            await tx.CreateSavepointAsync(savepoint, ct);
            hasSavepoint = true;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared({ConfigurationLock})", ct);
            await Lock("operation:" + request.OperationId.ToString("D"), ct);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request.Transfer))));
            var existing = await db.Operations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == request.OperationId, ct);
            if (existing != null)
            {
                if (existing.RequestHash != hash) throw new RiskLimitsException("IDEMPOTENCY_KEY_REUSED", "Operation ID was used with different content.");
                var replay = JsonSerializer.Deserialize<LimitConsumption>(existing.ResultJson)!;
                if (owned) await tx.CommitAsync(ct);
                else await tx.ReleaseSavepointAsync(savepoint, ct);
                hasSavepoint = false;
                return replay;
            }
            // Customer lock covers all wallets for tier/system/customer limits; wallet lock covers wallet overrides.
            foreach (var subject in new[] { $"customer:{request.Transfer.CustomerId:D}", $"wallet:{request.Transfer.WalletId:D}" }.Order(StringComparer.Ordinal))
                await Lock(subject, ct);
            var at = clock.GetUtcNow();
            var rules = await Matching(request.Transfer, at, ct);
            var usage = await ReadUsage(request.Transfer, rules, at, ct);
            var assessment = AssessmentEngine.Evaluate(request.Transfer, rules, usage, at);
            var result = new LimitConsumption(request.OperationId, assessment.RiskDecision == RiskDecision.Allow, assessment);
            if (result.Consumed)
            {
                foreach (var rule in rules.Where(x => x.Kind == RuleKind.Limit).OrderBy(x => x.Id))
                foreach (var period in RiyadhPeriods.At(at))
                {
                    var subject = AssessmentEngine.Subject(rule, request.Transfer);
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO risk_limits.period_usage ("Subject", "RuleVersionId", "Period", "PeriodStart", "PeriodEnd", "AmountMinor", "Count")
                        VALUES ({subject}, {rule.Id}, {period.Kind}, {period.Start}, {period.End}, {request.Transfer.AmountMinor}, 1)
                        ON CONFLICT ("Subject", "RuleVersionId", "Period", "PeriodStart") DO UPDATE
                        SET "AmountMinor" = risk_limits.period_usage."AmountMinor" + EXCLUDED."AmountMinor", "Count" = risk_limits.period_usage."Count" + 1
                        """, ct);
                }
            }
            db.Operations.Add(new UsageOperation { OperationId = request.OperationId, RequestHash = hash,
                CustomerId = request.Transfer.CustomerId, WalletId = request.Transfer.WalletId,
                Currency = request.Transfer.Currency, TransferType = request.Transfer.TransferType,
                AmountMinor = request.Transfer.AmountMinor, EvaluatedAt = at, Consumed = result.Consumed,
                ResultJson = JsonSerializer.Serialize(result) });
            await db.SaveChangesAsync(ct);
            if (owned) await tx.CommitAsync(ct);
            else await tx.ReleaseSavepointAsync(savepoint, ct);
            hasSavepoint = false;
            return result;
        }
        catch
        {
            try
            {
                if (owned) await tx.RollbackAsync(CancellationToken.None);
                else if (hasSavepoint) await tx.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            }
            catch (Exception)
            {
                // Preserve the original error if connection loss also prevents cleanup. The outer
                // coordinator must abandon an unusable transaction and retry the whole operation.
            }
            db.ChangeTracker.Clear();
            throw;
        }
        finally { if (owned) await tx.DisposeAsync(); }
    }
    private Task Lock(string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 6106))", ct);
    private Task<List<Rule>> Matching(AssessTransfer r, DateTimeOffset at, CancellationToken ct) => db.Rules.AsNoTracking()
        .Where(x => x.PublishedAt != null && x.EffectiveFrom <= at && (x.EffectiveTo == null || at < x.EffectiveTo) &&
            x.Currency == r.Currency && x.TransferType == r.TransferType &&
            (x.Scope == RuleScope.System || x.Scope == RuleScope.CustomerTier && x.ScopeKey == r.CustomerTier ||
             x.Scope == RuleScope.Customer && x.ScopeKey == r.CustomerId.ToString() || x.Scope == RuleScope.Wallet && x.ScopeKey == r.WalletId.ToString()))
        .ToListAsync(ct);
    private async Task<List<PeriodUsage>> ReadUsage(AssessTransfer r, IReadOnlyList<Rule> rules, DateTimeOffset at, CancellationToken ct)
    {
        // Receipts preserve subject-wide continuity across rule/tier changes. Per-version buckets remain
        // attribution records, never a way to reset a daily/monthly allowance by publishing a version.
        var result = new List<PeriodUsage>();
        foreach (var period in RiyadhPeriods.At(at))
        {
            var query = db.Operations.AsNoTracking().Where(x => x.Consumed && x.Currency == r.Currency && x.TransferType == r.TransferType &&
                x.EvaluatedAt >= period.Start && x.EvaluatedAt < period.End);
            var customers = await query.Where(x => x.CustomerId == r.CustomerId).GroupBy(x => x.CustomerId)
                .Select(g => new { Amount = g.Sum(x => (decimal)x.AmountMinor), Count = g.LongCount() }).SingleOrDefaultAsync(ct);
            var wallets = await query.Where(x => x.WalletId == r.WalletId).GroupBy(x => x.WalletId)
                .Select(g => new { Amount = g.Sum(x => (decimal)x.AmountMinor), Count = g.LongCount() }).SingleOrDefaultAsync(ct);
            foreach (var rule in rules.Where(x => x.Kind == RuleKind.Limit))
            {
                var values = rule.Scope == RuleScope.Wallet ? wallets : customers;
                if (values?.Amount > long.MaxValue) throw new RiskLimitsException("AMOUNT_OUT_OF_RANGE", "Usage cannot be represented.");
                result.Add(new PeriodUsage { Subject = AssessmentEngine.Subject(rule, r), RuleVersionId = rule.Id,
                    Period = period.Kind, PeriodStart = period.Start, PeriodEnd = period.End,
                    AmountMinor = (long)(values?.Amount ?? 0), Count = values?.Count ?? 0 });
            }
        }
        return result;
    }
    public async Task<Rule> CreateAsync(CreateRule request, Guid actor, CancellationToken ct = default)
    {
        RuleValidation.Validate(request);
        if (actor == Guid.Empty) throw new RiskLimitsException("VALIDATION_ERROR", "Actor is required.");
        var rule = new Rule
        {
            Id = Guid.NewGuid(), CreatedBy = actor, Kind = request.Kind, Scope = request.Scope, ScopeKey = request.ScopeKey,
            Version = request.Version, Currency = request.Currency, TransferType = request.TransferType,
            EffectiveFrom = request.EffectiveFrom, EffectiveTo = request.EffectiveTo, Reason = request.Reason,
            MinimumTransferMinor = request.MinimumTransferMinor, MaximumTransferMinor = request.MaximumTransferMinor,
            DailyAmountMinor = request.DailyAmountMinor, DailyCount = request.DailyCount,
            MonthlyAmountMinor = request.MonthlyAmountMinor, MonthlyCount = request.MonthlyCount,
            FixedFeeMinor = request.FixedFeeMinor, BasisPoints = request.BasisPoints,
            MinimumFeeMinor = request.MinimumFeeMinor, MaximumFeeMinor = request.MaximumFeeMinor,
            ReviewFromMinor = request.ReviewFromMinor, DenyFromMinor = request.DenyFromMinor
        };
        db.Rules.Add(rule);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        { db.ChangeTracker.Clear(); throw new RiskLimitsException("RULE_VERSION_CONFLICT", "Rule version already exists."); }
        return rule;
    }
    public async Task<Rule> PublishAsync(Guid id, Guid actor, CancellationToken ct = default)
    {
        if (actor == Guid.Empty) throw new RiskLimitsException("VALIDATION_ERROR", "Actor is required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ConfigurationLock})", ct);
        var rule = await db.Rules.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new RiskLimitsException("RULE_NOT_FOUND", "Rule not found.");
        if (rule.PublishedAt != null) throw new RiskLimitsException("STATE_CONFLICT", "Rule is already published.");
        rule.PublishedAt = clock.GetUtcNow(); rule.PublishedBy = actor;
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23P01" })
        { db.ChangeTracker.Clear(); throw new RiskLimitsException("RULE_EFFECTIVITY_OVERLAP", "Published rule periods overlap."); }
        return rule;
    }
    public async Task<Rule> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Rules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new RiskLimitsException("RULE_NOT_FOUND", "Rule not found.");
    public async Task<RulePage> ListAsync(RuleKind kind, RuleScope scope, string key, string currency, string transferType,
        int afterVersion, int pageSize, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind) || !RuleValidation.ValidKey(scope, key) || currency != "SAR" || transferType != "Internal" || afterVersion < 0 || pageSize < 1)
            throw new RiskLimitsException("VALIDATION_ERROR", "Invalid rule history query.");
        var size = Math.Min(pageSize, options.Value.MaxPageSize);
        var rows = await db.Rules.AsNoTracking().Where(x => x.Kind == kind && x.Scope == scope && x.ScopeKey == key &&
            x.Currency == currency && x.TransferType == transferType && x.Version > afterVersion).OrderBy(x => x.Version).Take(size + 1).ToListAsync(ct);
        return new(rows.Take(size).ToArray(), rows.Count > size ? rows[size - 1].Version : null);
    }
}
