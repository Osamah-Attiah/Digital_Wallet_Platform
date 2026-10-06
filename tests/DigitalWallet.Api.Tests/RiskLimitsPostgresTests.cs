using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using DigitalWallet.RiskLimits;
using DigitalWallet.RiskLimits.Application;
using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.RiskLimits.Domain;
using DigitalWallet.RiskLimits.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class RiskLimitsPostgresTests : IClassFixture<IdentityDatabaseFixture>, IAsyncLifetime
{
    private readonly IdentityPostgresFactory _factory;
    private readonly string _connection;
    private readonly Guid _actor = Guid.NewGuid();
    private readonly AssessTransfer _request = new(Guid.NewGuid(), Guid.NewGuid(), "Standard", 100, "SAR", "Internal");
    public RiskLimitsPostgresTests(IdentityDatabaseFixture fixture)
    { _connection = fixture.ConnectionString; _factory = new(_connection); }
    public Task InitializeAsync() => _factory.ResetAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();
    private async Task<T> Scope<T>(Func<IServiceProvider, Task<T>> action)
    { await using var s = _factory.Services.CreateAsyncScope(); return await action(s.ServiceProvider); }
    private Task<T> Db<T>(Func<RiskLimitsDbContext, Task<T>> action) => Scope(sp => action(sp.GetRequiredService<RiskLimitsDbContext>()));
    private CreateRule Definition(RuleKind kind = RuleKind.Limit, RuleScope scope = RuleScope.System, string key = "*", int version = 1) =>
        new(kind, scope, key, version, "SAR", "Internal", _factory.Time.GetUtcNow().AddDays(-1), null, "Phase six test",
            MaximumTransferMinor: kind == RuleKind.Limit ? long.MaxValue : null,
            FixedFeeMinor: kind == RuleKind.Fee ? 0 : null, BasisPoints: kind == RuleKind.Fee ? 0 : null);
    private Task<Rule> Create(CreateRule r) => Scope(sp => sp.GetRequiredService<IRuleAdministration>().CreateAsync(r, _actor));
    private Task<Rule> Publish(Guid id) => Scope(sp => sp.GetRequiredService<IRuleAdministration>().PublishAsync(id, _actor));
    private async Task<Rule> Rule(CreateRule r) => await Publish((await Create(r)).Id);
    private async Task Baseline(CreateRule? limit = null, CreateRule? fee = null, CreateRule? risk = null)
    { await Rule(limit ?? Definition()); await Rule(fee ?? Definition(RuleKind.Fee)); await Rule(risk ?? Definition(RuleKind.Risk)); }
    private Task<TransferAssessment> Quote(AssessTransfer? r = null) => Scope(sp => sp.GetRequiredService<ITransferAssessment>().AssessAsync(r ?? _request));
    private Task<LimitConsumption> Consume(AssessTransfer? r = null, Guid? id = null) => Scope(sp => sp.GetRequiredService<ILimitUsage>().ConsumeAsync(new(id ?? Guid.NewGuid(), r ?? _request)));

    [Fact]
    public async Task Empty_database_migration_and_model_match_and_all_entities_belong_to_own_schema()
    {
        await _factory.Services.InitializeRiskLimitsModuleAsync();
        await Db(async db =>
        {
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains("20260922120121_InitialRiskLimits", await db.Database.GetAppliedMigrationsAsync());
            Assert.All(db.Model.GetEntityTypes(), x => Assert.Equal("risk_limits", x.GetSchema()));
            Assert.Equal("bigint", db.Model.FindEntityType(typeof(PeriodUsage))!.FindProperty(nameof(PeriodUsage.AmountMinor))!.GetColumnType());
            return true;
        });
    }

    [Theory]
    [InlineData("UPDATE risk_limits.rules SET \"Reason\" = 'changed' WHERE \"Id\" = @id")]
    [InlineData("UPDATE risk_limits.rules SET \"PublishedAt\" = NULL, \"PublishedBy\" = NULL WHERE \"Id\" = @id")]
    [InlineData("DELETE FROM risk_limits.rules WHERE \"Id\" = @id")]
    [InlineData("TRUNCATE risk_limits.rules CASCADE")]
    public async Task PostgreSQL_prohibits_published_rule_mutation(string sql)
    {
        var rule = await Rule(Definition());
        await using var connection = new NpgsqlConnection(_connection); await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection); cmd.Parameters.AddWithValue("id", rule.Id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", error.SqlState);
    }
    [Fact]
    public async Task PostgreSQL_exclusion_rejects_overlap_even_with_direct_concurrent_publication()
    {
        var first = await Create(Definition()); var second = await Create(Definition(version: 2));
        async Task<string> DirectPublish(Guid id)
        {
            await using var c = new NpgsqlConnection(_connection); await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE risk_limits.rules SET \"PublishedAt\" = now(), \"PublishedBy\" = @actor WHERE \"Id\" = @id", c);
            cmd.Parameters.AddWithValue("actor", _actor); cmd.Parameters.AddWithValue("id", id);
            try { await cmd.ExecuteNonQueryAsync(); return "published"; } catch (PostgresException ex) { return ex.SqlState; }
        }
        var results = await Task.WhenAll(DirectPublish(first.Id), DirectPublish(second.Id));
        Assert.Single(results, x => x == "published"); Assert.Single(results, x => x == "23P01");
    }
    [Fact]
    public async Task Adjacent_versions_are_allowed_and_exact_boundary_uses_new_version()
    {
        var now = _factory.Time.GetUtcNow();
        var old = await Rule(Definition() with { EffectiveTo = now, MaximumTransferMinor = 99 });
        var next = await Rule(Definition(version: 2) with { EffectiveFrom = now, MaximumTransferMinor = 100 });
        await Rule(Definition(RuleKind.Fee)); await Rule(Definition(RuleKind.Risk));
        var result = await Quote(); Assert.Equal(RiskDecision.Allow, result.RiskDecision);
        Assert.Contains(new RuleVersion(next.Id, 2), result.LimitRuleVersions);
        Assert.DoesNotContain(new RuleVersion(old.Id, 1), result.LimitRuleVersions);
    }
    [Fact]
    public async Task All_scopes_combine_most_restrictive_limits_and_ignore_other_subjects_and_drafts()
    {
        await Baseline(Definition() with { MinimumTransferMinor = 10, MaximumTransferMinor = 1000, DailyAmountMinor = 1000 });
        await Rule(Definition(scope: RuleScope.CustomerTier, key: "Standard") with { MinimumTransferMinor = 20, MaximumTransferMinor = 800, DailyCount = 8 });
        await Rule(Definition(scope: RuleScope.Customer, key: _request.CustomerId.ToString()) with { MaximumTransferMinor = 500, MonthlyAmountMinor = 5000 });
        await Rule(Definition(scope: RuleScope.Wallet, key: _request.WalletId.ToString()) with { MinimumTransferMinor = 30, MaximumTransferMinor = 900, MonthlyCount = 20 });
        await Rule(Definition(scope: RuleScope.Customer, key: Guid.NewGuid().ToString()) with { MaximumTransferMinor = 1 });
        await Create(Definition(version: 2) with { MaximumTransferMinor = 1 });
        var q = await Quote();
        Assert.Equal(new EffectiveLimits(30, 500, 1000, 8, 5000, 20), q.EffectiveLimits);
        Assert.Equal(4, q.LimitRuleVersions.Count);
    }
    [Theory]
    [InlineData(99, RiskDecision.Deny)]
    [InlineData(100, RiskDecision.Allow)]
    [InlineData(200, RiskDecision.Allow)]
    [InlineData(201, RiskDecision.Deny)]
    public async Task Minimum_maximum_and_one_halalah_boundary(long amount, RiskDecision decision)
    {
        await Baseline(Definition() with { MinimumTransferMinor = 100, MaximumTransferMinor = 200 });
        var q = await Quote(_request with { AmountMinor = amount }); Assert.Equal(decision, q.RiskDecision);
        Assert.Contains(decision == RiskDecision.Allow ? "RISK_ALLOWED" : "TRANSFER_AMOUNT_LIMIT_EXCEEDED", q.ReasonCodes);
    }
    [Theory]
    [InlineData("day-amount", "DAILY_LIMIT_EXCEEDED")]
    [InlineData("day-count", "DAILY_LIMIT_EXCEEDED")]
    [InlineData("month-amount", "MONTHLY_LIMIT_EXCEEDED")]
    [InlineData("month-count", "MONTHLY_LIMIT_EXCEEDED")]
    public async Task Period_amount_and_count_limits_reject_without_consumption(string kind, string reason)
    {
        var d = Definition();
        d = kind switch { "day-amount" => d with { DailyAmountMinor = 100 }, "day-count" => d with { DailyCount = 1 },
            "month-amount" => d with { MonthlyAmountMinor = 100 }, _ => d with { MonthlyCount = 1 } };
        await Baseline(d);
        Assert.True((await Consume()).Consumed);
        var rejected = await Consume(_request with { AmountMinor = 1 }); Assert.False(rejected.Consumed);
        Assert.Contains(reason, rejected.Assessment.ReasonCodes);
        Assert.Equal(2, await Db(db => db.Operations.CountAsync()));
        Assert.All(await Db(db => db.Usage.ToListAsync()), u => { Assert.Equal(100, u.AmountMinor); Assert.Equal(1, u.Count); });
    }
    [Theory]
    [InlineData("2026-01-31T20:59:59Z", "2026-01-30T21:00:00Z", "2026-01-31T21:00:00Z", "2025-12-31T21:00:00Z", "2026-01-31T21:00:00Z")]
    [InlineData("2026-01-31T21:00:00Z", "2026-01-31T21:00:00Z", "2026-02-01T21:00:00Z", "2026-01-31T21:00:00Z", "2026-02-28T21:00:00Z")]
    [InlineData("2028-02-29T12:00:00Z", "2028-02-28T21:00:00Z", "2028-02-29T21:00:00Z", "2028-01-31T21:00:00Z", "2028-02-29T21:00:00Z")]
    public async Task Riyadh_periods_are_half_open_UTC_and_persist_exact_calendar_boundaries(string at, string ds, string de, string ms, string me)
    {
        _factory.Time.SetUtcNow(DateTimeOffset.Parse(at)); await Baseline(); await Consume();
        var usage = await Db(db => db.Usage.ToListAsync());
        var day = Assert.Single(usage, x => x.Period == "Day"); var month = Assert.Single(usage, x => x.Period == "Month");
        Assert.Equal(DateTimeOffset.Parse(ds), day.PeriodStart); Assert.Equal(DateTimeOffset.Parse(de), day.PeriodEnd);
        Assert.Equal(DateTimeOffset.Parse(ms), month.PeriodStart); Assert.Equal(DateTimeOffset.Parse(me), month.PeriodEnd);
    }
    [Fact]
    public async Task New_day_resets_only_daily_bucket_and_month_changes_reset_monthly_bucket()
    {
        _factory.Time.SetUtcNow(DateTimeOffset.Parse("2026-01-29T20:59:59Z"));
        await Baseline(Definition() with { DailyCount = 1, MonthlyCount = 2 }); await Consume();
        _factory.Time.Advance(TimeSpan.FromSeconds(1)); Assert.True((await Consume()).Consumed);
        _factory.Time.Advance(TimeSpan.FromDays(1)); Assert.False((await Consume()).Consumed);
        _factory.Time.Advance(TimeSpan.FromDays(1)); Assert.True((await Consume()).Consumed);
    }
    [Theory]
    [InlineData(100, 7, 0, null, null, 7)]
    [InlineData(10000, 0, 125, null, null, 125)]
    [InlineData(100, 0, 1, 3L, null, 3)]
    [InlineData(10000, 4, 500, null, 25L, 25)]
    [InlineData(100, 0, 0, null, null, 0)]
    [InlineData(49, 0, 100, null, null, 0)]
    [InlineData(50, 0, 100, null, null, 1)]
    [InlineData(150, 2, 100, null, null, 4)]
    [InlineData(51, 0, 100, null, null, 1)]
    public async Task Fee_fixed_percentage_clamps_zero_and_rounding_preserve_version(long amount, long fixedFee, int bps, long? min, long? max, long expected)
    {
        await Baseline(fee: Definition(RuleKind.Fee) with { FixedFeeMinor = fixedFee, BasisPoints = bps, MinimumFeeMinor = min, MaximumFeeMinor = max });
        var q = await Quote(_request with { AmountMinor = amount }); Assert.Equal(expected, q.FeeMinor);
        Assert.Equal(amount + expected, q.TotalDebitMinor); Assert.NotEqual(Guid.Empty, q.FeeRuleVersion.Id); Assert.Equal(1, q.FeeRuleVersion.Version);
    }
    [Fact]
    public async Task Most_specific_fee_rule_wins_including_explicit_zero_override()
    {
        await Baseline(fee: Definition(RuleKind.Fee) with { FixedFeeMinor = 10 });
        await Rule(Definition(RuleKind.Fee, RuleScope.CustomerTier, "Standard") with { FixedFeeMinor = 8 });
        await Rule(Definition(RuleKind.Fee, RuleScope.Customer, _request.CustomerId.ToString()) with { FixedFeeMinor = 5 });
        var winner = await Rule(Definition(RuleKind.Fee, RuleScope.Wallet, _request.WalletId.ToString()));
        var q = await Quote(); Assert.Equal(0, q.FeeMinor); Assert.Equal(winner.Id, q.FeeRuleVersion.Id);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(long.MinValue)]
    public async Task Nonpositive_amount_is_rejected_without_usage(long amount)
    {
        await Baseline(); var error = await Assert.ThrowsAsync<RiskLimitsException>(() => Consume(_request with { AmountMinor = amount }));
        Assert.Equal("VALIDATION_ERROR", error.Code); Assert.Equal(0, await Db(db => db.Usage.CountAsync()));
    }
    [Theory]
    [InlineData("USD")] [InlineData("sar")] [InlineData("")]
    public async Task Wrong_currency_is_rejected(string currency)
    {
        await Baseline(); var e = await Assert.ThrowsAsync<RiskLimitsException>(() => Quote(_request with { Currency = currency })); Assert.Equal("VALIDATION_ERROR", e.Code);
    }
    [Theory]
    [InlineData(0, 1)] [InlineData(10000, long.MaxValue)]
    public async Task Fee_or_total_overflow_is_detected_before_bigint_conversion(int bps, long fixedFee)
    {
        await Baseline(fee: Definition(RuleKind.Fee) with { FixedFeeMinor = fixedFee, BasisPoints = bps });
        var e = await Assert.ThrowsAsync<RiskLimitsException>(() => Consume(_request with { AmountMinor = long.MaxValue }));
        Assert.Equal("AMOUNT_OUT_OF_RANGE", e.Code); Assert.Equal(0, await Db(db => db.Operations.CountAsync()));
    }
    [Fact]
    public async Task Decimal_intermediate_can_be_clamped_before_long_conversion()
    {
        await Baseline(fee: Definition(RuleKind.Fee) with { FixedFeeMinor = long.MaxValue, BasisPoints = 10000, MaximumFeeMinor = 0 });
        var q = await Quote(_request with { AmountMinor = long.MaxValue }); Assert.Equal(long.MaxValue, q.TotalDebitMinor); Assert.Equal(0, q.FeeMinor);
    }
    [Theory]
    [InlineData(99, RiskDecision.Allow, "RISK_ALLOWED")]
    [InlineData(100, RiskDecision.Review, "RISK_REVIEW_REQUIRED")]
    [InlineData(200, RiskDecision.Deny, "RISK_REJECTED")]
    public async Task Risk_decisions_retain_safe_reason_and_rule_version(long amount, RiskDecision decision, string reason)
    {
        await Baseline(risk: Definition(RuleKind.Risk) with { ReviewFromMinor = 100, DenyFromMinor = 200 });
        var q = await Quote(_request with { AmountMinor = amount }); Assert.Equal(decision, q.RiskDecision); Assert.Contains(reason, q.ReasonCodes);
        Assert.Equal(1, Assert.Single(q.RiskRuleVersions).Version);
        var consume = await Consume(_request with { AmountMinor = amount }); Assert.Equal(decision == RiskDecision.Allow, consume.Consumed);
    }
    [Fact]
    public async Task Risk_override_cannot_relax_system_deny()
    {
        await Baseline(risk: Definition(RuleKind.Risk) with { DenyFromMinor = 100 });
        await Rule(Definition(RuleKind.Risk, RuleScope.Customer, _request.CustomerId.ToString()));
        var q = await Quote(); Assert.Equal(RiskDecision.Deny, q.RiskDecision); Assert.Equal(2, q.RiskRuleVersions.Count);
    }
    [Fact]
    public async Task Quote_is_read_only_and_repeated_quotes_do_not_reserve_usage_or_create_financial_records()
    {
        await Baseline(Definition() with { DailyCount = 1 });
        for (var i = 0; i < 4; i++) Assert.Equal(RiskDecision.Allow, (await Quote()).RiskDecision);
        Assert.Equal(0, await Db(db => db.Usage.CountAsync())); Assert.Equal(0, await Db(db => db.Operations.CountAsync()));
        Assert.Equal(0, await Scope(sp => sp.GetRequiredService<DigitalWallet.Ledger.Infrastructure.LedgerDbContext>().Journals.CountAsync()));
    }
    [Fact]
    public async Task Concurrent_idempotent_retries_return_original_snapshot_even_after_time_changes()
    {
        await Baseline(Definition() with { DailyCount = 1 }); var id = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Consume(id: id)));
        Assert.All(results, r => Assert.True(r.Consumed));
        Assert.Single(results.Select(x => JsonSerializer.Serialize(x)).Distinct());
        _factory.Time.Advance(TimeSpan.FromDays(40)); var replay = await Consume(id: id);
        Assert.Equal(JsonSerializer.Serialize(results[0]), JsonSerializer.Serialize(replay));
        Assert.Equal(1, await Db(db => db.Operations.CountAsync()));
        Assert.All(await Db(db => db.Usage.ToListAsync()), u => Assert.Equal(1, u.Count));
    }
    [Theory]
    [InlineData("amount")] [InlineData("wallet")] [InlineData("customer")] [InlineData("tier")]
    public async Task Reusing_operation_with_different_content_is_conflict(string field)
    {
        await Baseline(); var id = Guid.NewGuid(); await Consume(id: id);
        var changed = field switch { "amount" => _request with { AmountMinor = 101 }, "wallet" => _request with { WalletId = Guid.NewGuid() },
            "customer" => _request with { CustomerId = Guid.NewGuid() }, _ => _request with { CustomerTier = "Other" } };
        var e = await Assert.ThrowsAsync<RiskLimitsException>(() => Consume(changed, id)); Assert.Equal("IDEMPOTENCY_KEY_REUSED", e.Code);
        Assert.Equal(1, await Db(db => db.Operations.CountAsync()));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task One_hundred_competing_operations_never_exceed_limits_across_wallets(bool countLimit)
    {
        await Baseline(Definition() with { DailyAmountMinor = countLimit ? null : 1000, DailyCount = countLimit ? 10 : null });
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Consume(_request with { WalletId = Guid.NewGuid() })));
        Assert.Equal(10, results.Count(x => x.Consumed)); Assert.Equal(90, results.Count(x => !x.Consumed));
        Assert.All(await Db(db => db.Usage.ToListAsync()), u => { Assert.Equal(1000, u.AmountMinor); Assert.Equal(10, u.Count); });
        Assert.Equal(10, await Db(db => db.Operations.CountAsync(x => x.Consumed)));
    }
    [Fact]
    public async Task Failed_outer_transaction_rolls_back_usage_and_receipt_and_retry_can_consume()
    {
        await Baseline(); var id = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(_connection))
        {
            await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
            await using var participant = await RiskLimitsTransactionParticipant.EnlistAsync(connection, tx, _factory.Time, Options.Create(new RiskLimitsOptions()));
            Assert.True((await participant.Usage.ConsumeAsync(new(id, _request))).Consumed);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await Db(db => db.Usage.CountAsync())); Assert.Equal(0, await Db(db => db.Operations.CountAsync()));
        Assert.True((await Consume(id: id)).Consumed);
    }
    [Fact]
    public async Task Failed_participant_rolls_back_to_savepoint_without_aborting_callers_transaction()
    {
        await Baseline();
        // Force a receipt insertion failure after usage upserts to exercise partial-work rollback.
        await Db(async db => { await db.Database.ExecuteSqlRawAsync("ALTER TABLE risk_limits.usage_operations ADD CONSTRAINT injected_failure CHECK (false)"); return true; });
        try
        {
            await using var connection = new NpgsqlConnection(_connection); await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
            await using var participant = await RiskLimitsTransactionParticipant.EnlistAsync(connection, tx, _factory.Time, Options.Create(new RiskLimitsOptions()));
            await Assert.ThrowsAsync<DbUpdateException>(() => participant.Usage.ConsumeAsync(new(Guid.NewGuid(), _request)));
            await tx.CommitAsync();
            Assert.Equal(0, await Db(db => db.Usage.CountAsync()));
        }
        finally { await Db(async db => { await db.Database.ExecuteSqlRawAsync("ALTER TABLE risk_limits.usage_operations DROP CONSTRAINT injected_failure"); return true; }); }
    }
    [Fact]
    public async Task Missing_rules_fail_closed()
    {
        var ex = await Assert.ThrowsAsync<RiskLimitsException>(() => Quote()); Assert.Equal("RULES_NOT_CONFIGURED", ex.Code);
    }
    [Theory]
    [InlineData("RiskLimits:MaxPageSize", "0")] [InlineData("RiskLimits:MaxPageSize", "201")]
    [InlineData("RiskLimits:BusinessTimeZone", "UTC")]
    public async Task Invalid_options_fail_at_startup(string key, string value)
    {
        await using var invalid = new IdentityPostgresFactory(_connection, new Dictionary<string, string?> { [key] = value });
        Assert.Throws<OptionsValidationException>(() => invalid.CreateClient());
    }

    [Fact]
    public async Task Published_version_changes_and_new_overrides_do_not_reset_period_allowance()
    {
        var boundary = _factory.Time.GetUtcNow().AddMinutes(1);
        await Baseline(Definition() with { DailyAmountMinor = 150, EffectiveTo = boundary });
        await Rule(Definition(version: 2) with { EffectiveFrom = boundary, DailyAmountMinor = 150 });
        await Consume(); _factory.Time.SetUtcNow(boundary);
        Assert.False((await Consume()).Consumed);
        var walletRule = await Rule(Definition(scope: RuleScope.Wallet, key: _request.WalletId.ToString()) with { DailyCount = 1 });
        var q = await Quote(_request with { AmountMinor = 1 });
        Assert.Equal(RiskDecision.Deny, q.RiskDecision); Assert.Contains("DAILY_LIMIT_EXCEEDED", q.ReasonCodes);
        Assert.Contains(new RuleVersion(walletRule.Id, 1), q.LimitRuleVersions);
    }
    [Fact]
    public async Task Rejected_operation_is_idempotent_but_does_not_consume_usage()
    {
        await Baseline(risk: Definition(RuleKind.Risk) with { ReviewFromMinor = 1 });
        var id = Guid.NewGuid(); var rejected = await Consume(id: id); Assert.False(rejected.Consumed);
        Assert.Equal(JsonSerializer.Serialize(rejected), JsonSerializer.Serialize(await Consume(id: id)));
        var ex = await Assert.ThrowsAsync<RiskLimitsException>(() => Consume(_request with { AmountMinor = 2 }, id));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", ex.Code); Assert.Equal(0, await Db(db => db.Usage.CountAsync()));
        Assert.Equal(1, await Db(db => db.Operations.CountAsync()));
    }
    [Fact]
    public async Task Enlisted_success_is_invisible_until_outer_commit_and_keeps_connection_open()
    {
        await Baseline(); await using var connection = new NpgsqlConnection(_connection); await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var participant = await RiskLimitsTransactionParticipant.EnlistAsync(connection, tx, _factory.Time, Options.Create(new RiskLimitsOptions())))
        {
            Assert.True((await participant.Usage.ConsumeAsync(new(Guid.NewGuid(), _request))).Consumed);
            Assert.Equal(0, await Db(db => db.Operations.CountAsync()));
        }
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await tx.CommitAsync(); Assert.Equal(1, await Db(db => db.Operations.CountAsync()));
    }
    [Fact]
    public async Task Rule_publication_cannot_change_an_in_flight_consumptions_configuration()
    {
        await Baseline(); var draft = await Create(Definition(RuleKind.Risk, RuleScope.Customer, _request.CustomerId.ToString()) with { DenyFromMinor = 1 });
        await using var connection = new NpgsqlConnection(_connection); await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
        await using var participant = await RiskLimitsTransactionParticipant.EnlistAsync(connection, tx, _factory.Time, Options.Create(new RiskLimitsOptions()));
        Assert.True((await participant.Usage.ConsumeAsync(new(Guid.NewGuid(), _request))).Consumed);
        var publication = Publish(draft.Id);
        // Verify a real database waiter instead of assuming overlap from task scheduling.
        var deadline = DateTime.UtcNow.AddSeconds(10); var waiting = false;
        while (DateTime.UtcNow < deadline && !waiting)
        {
            await using var probe = new NpgsqlConnection(_connection); await probe.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory' AND query LIKE '%pg_advisory_xact_lock(%')", probe);
            waiting = (bool)(await cmd.ExecuteScalarAsync())!;
            if (!waiting) await Task.Delay(20);
        }
        Assert.True(waiting); Assert.False(publication.IsCompleted);
        await tx.CommitAsync(); await publication;
        Assert.Equal(RiskDecision.Deny, (await Quote()).RiskDecision);
    }
    [Fact]
    public async Task Usage_overflow_is_safe_and_other_subjects_have_independent_allowances()
    {
        await Baseline(); Assert.True((await Consume(_request with { AmountMinor = long.MaxValue })).Consumed);
        var denied = await Consume(_request with { AmountMinor = 1 }); Assert.False(denied.Consumed);
        Assert.Contains("AMOUNT_OUT_OF_RANGE", denied.Assessment.ReasonCodes);
        Assert.True((await Consume(_request with { CustomerId = Guid.NewGuid(), WalletId = Guid.NewGuid() })).Consumed);
    }
    [Theory]
    [InlineData("negative")] [InlineData("min-max")] [InlineData("bps")] [InlineData("period")]
    [InlineData("scope")] [InlineData("kind")] [InlineData("reason")] [InlineData("fee-fields")]
    public async Task Invalid_rule_definitions_are_rejected(string field)
    {
        var rule = field switch
        {
            "negative" => Definition() with { DailyCount = -1 },
            "min-max" => Definition() with { MinimumTransferMinor = 2, MaximumTransferMinor = 1 },
            "bps" => Definition(RuleKind.Fee) with { BasisPoints = 10001 },
            "period" => Definition() with { EffectiveTo = _factory.Time.GetUtcNow().AddDays(-2) },
            "scope" => Definition(scope: RuleScope.Customer, key: "*"),
            "kind" => Definition() with { Kind = (RuleKind)100 },
            "reason" => Definition() with { Reason = " " },
            _ => Definition() with { FixedFeeMinor = 0 }
        };
        var e = await Assert.ThrowsAsync<RiskLimitsException>(() => Create(rule)); Assert.Equal("VALIDATION_ERROR", e.Code);
    }
    [Fact]
    public async Task Database_checks_reject_invalid_draft_values()
    {
        var draft = await Create(Definition());
        var e = await Assert.ThrowsAsync<PostgresException>(() => Db(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE risk_limits.rules SET \"DailyCount\" = -1 WHERE \"Id\" = {draft.Id}")));
        Assert.Equal("23514", e.SqlState);
    }
    private sealed record ChallengeResponse(Guid ChallengeId);
    private async Task<(HttpClient Client, Guid User)> Authenticate(string label)
    {
        var client = _factory.CreateClient(); var device = new DeviceRequest(label, "Risk tests");
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(label + "@example.test", "Password!12345", true, "ar-SA", device));
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var challenge = (await register.Content.ReadFromJsonAsync<ChallengeResponse>())!.ChallengeId;
        var response = await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, _factory.Otp.Get(challenge), device));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResult>())!; client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        return (client, Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken).Subject));
    }
    private Task<bool> Grant(Guid user, params string[] permissions) => Scope(async sp =>
    {
        var db = sp.GetRequiredService<IdentityDbContext>(); var id = Guid.NewGuid();
        db.Roles.Add(new ApplicationRole { Id = id, Name = id.ToString(), NormalizedName = id.ToString(), DisplayName = "Risk role" });
        db.UserRoles.Add(new ApplicationUserRole { UserId = user, RoleId = id });
        foreach (var permission in permissions) db.RolePermissions.Add(new RolePermission { RoleId = id, PermissionCode = permission });
        await db.SaveChangesAsync(); return true;
    });
    [Fact]
    public async Task Anonymous_and_customers_cannot_access_or_modify_arbitrary_rule_ids()
    {
        var rule = await Create(Definition(scope: RuleScope.Customer, key: _request.CustomerId.ToString()));
        using var anonymous = _factory.CreateClient();
        var (customer, _) = await Authenticate("risk-customer"); using var owner = customer;
        foreach (var id in new[] { rule.Id, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/admin/risk-limits/rules/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/v1/admin/risk-limits/rules/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/api/v1/admin/risk-limits/rules/{id}/publish", null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync("/api/v1/admin/risk-limits/rules", Definition())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/admin/risk-limits/rules?kind=Limit&scope=System&key=*&currency=SAR&transferType=Internal")).StatusCode);
        Assert.Null((await Db(db => db.Rules.SingleAsync(x => x.Id == rule.Id))).PublishedAt);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsJsonAsync("/api/v1/risk-limits/assessments", _request)).StatusCode);
    }
    [Theory]
    [InlineData("risk.rules.create")] [InlineData("risk.rules.publish")] [InlineData("risk.rules.read")]
    public async Task Administrative_permissions_are_separate(string permission)
    {
        var (client, user) = await Authenticate("permission"); using var http = client; await Grant(user, permission);
        var draft = await Create(Definition());
        var create = await http.PostAsJsonAsync("/api/v1/admin/risk-limits/rules", Definition(version: 2));
        Assert.Equal(permission == "risk.rules.create" ? HttpStatusCode.Created : HttpStatusCode.Forbidden, create.StatusCode);
        var publish = await http.PostAsync($"/api/v1/admin/risk-limits/rules/{draft.Id}/publish", null);
        Assert.Equal(permission == "risk.rules.publish" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, publish.StatusCode);
        var read = await http.GetAsync($"/api/v1/admin/risk-limits/rules/{draft.Id}");
        Assert.Equal(permission == "risk.rules.read" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, read.StatusCode);
    }
    [Fact]
    public async Task Admin_errors_pagination_and_immutable_endpoint_surface_follow_contract()
    {
        var (client, user) = await Authenticate("admin"); using var http = client;
        await Grant(user, "risk.rules.create", "risk.rules.publish", "risk.rules.read");
        var draft = await Create(Definition()); await Publish(draft.Id);
        var invalid = await http.PostAsJsonAsync("/api/v1/admin/risk-limits/rules", Definition() with { MaximumTransferMinor = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        var body = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("traceId", out _)); Assert.True(body.TryGetProperty("correlationId", out _));
        var duplicate = await http.PostAsJsonAsync("/api/v1/admin/risk-limits/rules", Definition());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var overlapping = await Create(Definition(version: 2));
        var conflict = await http.PostAsync($"/api/v1/admin/risk-limits/rules/{overlapping.Id}/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("RULE_EFFECTIVITY_OVERLAP", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/v1/admin/risk-limits/rules/{draft.Id}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v1/admin/risk-limits/rules/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.DeleteAsync($"/api/v1/admin/risk-limits/rules/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PutAsJsonAsync($"/api/v1/admin/risk-limits/rules/{draft.Id}", Definition())).StatusCode);
        for (var version = 3; version <= 103; version++) await Create(Definition(version: version));
        var page = (await http.GetFromJsonAsync<RulePage>("/api/v1/admin/risk-limits/rules?kind=Limit&scope=System&key=*&currency=SAR&transferType=Internal&pageSize=10000"))!;
        Assert.Equal(100, page.Items.Count); Assert.Equal(100, page.NextAfterVersion);
        var next = (await http.GetFromJsonAsync<RulePage>("/api/v1/admin/risk-limits/rules?kind=Limit&scope=System&key=*&currency=SAR&transferType=Internal&pageSize=10000&afterVersion=100"))!;
        Assert.Equal(new[] { 101, 102, 103 }, next.Items.Select(x => x.Version)); Assert.Null(next.NextAfterVersion);
    }
}
