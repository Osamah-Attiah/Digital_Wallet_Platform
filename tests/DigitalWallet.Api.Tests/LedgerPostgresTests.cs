using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalWallet.Customers.Domain;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using DigitalWallet.Ledger;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Ledger.Domain;
using DigitalWallet.Ledger.Infrastructure;
using DigitalWallet.Wallets;
using DigitalWallet.Wallets.Domain;
using DigitalWallet.Wallets.Persistence;
using DigitalWallet.Wallets.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class LedgerPostgresTests : IClassFixture<IdentityDatabaseFixture>, IAsyncLifetime
{
    private readonly IdentityPostgresFactory _factory;
    private readonly string _connection;
    public LedgerPostgresTests(IdentityDatabaseFixture fixture)
    {
        _connection = fixture.ConnectionString;
        _factory = new IdentityPostgresFactory(_connection);
    }
    public Task InitializeAsync() => _factory.ResetAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Empty_database_migrations_have_no_pending_changes_and_seed_the_chart_idempotently()
    {
        await _factory.Services.InitializeLedgerModuleAsync();
        await _factory.Services.InitializeLedgerModuleAsync();
        await Database(async db =>
        {
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains("20260920121221_InitialLedger", await db.Database.GetAppliedMigrationsAsync());
            Assert.All(db.Model.GetEntityTypes(), x => Assert.Equal("ledger", x.GetSchema()));
            Assert.Equal(3, await db.Accounts.CountAsync());
            Assert.Equal(3, await db.Balances.CountAsync());
            Assert.Equal(new[] { AccountKind.AdjustmentControl, AccountKind.PlatformFeeRevenue, AccountKind.Suspense },
                (await db.Accounts.Select(x => x.Kind).ToArrayAsync()).OrderBy(x => x));
            Assert.Equal("bigint", db.Model.FindEntityType(typeof(LedgerEntry))!.FindProperty(nameof(LedgerEntry.AmountMinor))!.GetColumnType());
        });
    }

    [Fact]
    public async Task Concurrent_account_provisioning_creates_exactly_one_account_and_projection_per_wallet_currency()
    {
        var wallet = Guid.NewGuid();
        var ids = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            Scope(sp => sp.GetRequiredService<ILedgerAccounts>().EnsureWalletAccountAsync(wallet, "SAR"))));
        Assert.Single(ids.Distinct());
        await Database(async db =>
        {
            Assert.Equal(1, await db.Accounts.CountAsync(x => x.WalletId == wallet));
            Assert.Equal(1, await db.Balances.CountAsync(x => x.AccountId == ids[0]));
        });
    }

    [Fact]
    public async Task Funding_100_SAR_posts_10000_minor_units_with_balanced_immutable_history()
    {
        var (wallet, account, control) = await Accounts();
        var posted = await Post(Funding(account, control, 10000));
        var balance = await Balance(wallet);
        Assert.Equal(10000, balance.LedgerBalance);
        Assert.Equal(balance.LedgerBalance, balance.AvailableBalance);
        Assert.Equal(1, balance.Version);
        await Database(async db =>
        {
            var entries = await db.Entries.Where(x => x.JournalId == posted.Id).ToListAsync();
            Assert.Equal(2, entries.Count);
            Assert.Equal(entries.Where(x => x.Side == EntrySide.Debit).Sum(x => x.AmountMinor),
                entries.Where(x => x.Side == EntrySide.Credit).Sum(x => x.AmountMinor));
            Assert.Equal(JournalStatus.Posted, (await db.Journals.SingleAsync()).Status);
            Assert.Equal(10000, (await db.Balances.SingleAsync(x => x.AccountId == control)).BalanceMinor);
        });
        Assert.Empty((await Reconcile()).Findings);
    }

    [Fact]
    public async Task Concurrent_same_source_retries_post_once_and_reordered_lines_replay_the_same_result()
    {
        var (wallet, account, control) = await Accounts();
        var request = Funding(account, control, 10000);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Post(request)));
        Assert.Single(results.Select(x => x.Id).Distinct());
        Assert.Equal(results[0], await Post(request with { Entries = request.Entries.Reverse().ToArray() }));
        Assert.Equal(10000, (await Balance(wallet)).LedgerBalance);
        await Database(async db => Assert.Equal(1, await db.Journals.CountAsync()));
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("account")]
    [InlineData("reason")]
    [InlineData("actor")]
    public async Task Source_key_with_different_content_is_a_conflict(string change)
    {
        var (wallet, account, control) = await Accounts();
        var request = Funding(account, control, 10000);
        await Post(request);
        var other = await Scope(sp => sp.GetRequiredService<ILedgerAccounts>().EnsureWalletAccountAsync(Guid.NewGuid(), "SAR"));
        var changed = change switch
        {
            "amount" => request with { Entries = [new(control, PostingSide.Debit, 1), new(account, PostingSide.Credit, 1)] },
            "account" => request with { Entries = [new(control, PostingSide.Debit, 10000), new(other, PostingSide.Credit, 10000)] },
            "actor" => request with { ActorId = Guid.NewGuid() },
            _ => request with { Reason = "different reason" }
        };
        var error = await Assert.ThrowsAsync<LedgerException>(() => Post(changed));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", error.Code);
        Assert.Equal(10000, (await Balance(wallet)).LedgerBalance);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(100, 99)]
    public async Task Application_rejects_nonpositive_or_unbalanced_journals(long debit, long credit)
    {
        var (_, account, control) = await Accounts();
        var error = await Assert.ThrowsAsync<LedgerException>(() => Post(Funding(account, control, debit) with
            { Entries = [new(control, PostingSide.Debit, debit), new(account, PostingSide.Credit, credit)] }));
        Assert.Equal("JOURNAL_UNBALANCED", error.Code);
        await Database(async db => Assert.Empty(await db.Journals.ToListAsync()));
    }

    [Theory]
    [InlineData("unbalanced", "ledger_balanced")]
    [InlineData("empty", "ledger_balanced")]
    [InlineData("draft", "ledger_posting_boundary")]
    [InlineData("posted-insert", "ledger_posting_boundary")]
    [InlineData("zero", "ck_entry_positive")]
    [InlineData("negative", "ck_entry_positive")]
    [InlineData("side", "ck_entry_side")]
    public async Task PostgreSQL_rejects_invalid_journals_even_when_application_is_bypassed(string invalid, string constraint)
    {
        var (_, account, control) = await Accounts();
        await Database(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var id = Guid.NewGuid();
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await InsertDraft(db, id, invalid == "posted-insert" ? "Posted" : "Draft");
                if (invalid is not ("empty" or "draft"))
                {
                    await InsertLine(db, id, control, invalid == "negative" ? -1 : invalid == "zero" ? 0 : 100,
                        invalid == "side" ? "Wrong" : "Debit");
                    await InsertLine(db, id, account, invalid == "unbalanced" ? 99 : 100, "Credit");
                }
                if (invalid != "draft") await Approve(db, id);
                await tx.CommitAsync();
            });
            Assert.Equal(constraint, error.ConstraintName);
        });
        await Database(async db => Assert.Empty(await db.Journals.ToListAsync()));
    }

    [Fact]
    public async Task Currency_mixing_is_rejected_in_application_and_by_composite_foreign_keys()
    {
        var (_, account, _) = await Accounts();
        var usd = await Scope(sp => sp.GetRequiredService<ILedgerAccounts>().EnsureSystemAccountAsync(SystemAccount.AdjustmentControl, "USD"));
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<LedgerException>(() => Post(Funding(account, usd, 100)))).Code);
        await Database(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var id = Guid.NewGuid();
            await InsertDraft(db, id);
            var error = await Assert.ThrowsAsync<PostgresException>(() => InsertLine(db, id, usd, 100, "Debit"));
            Assert.Equal("23503", error.SqlState);
        });
        Assert.Equal("VALIDATION_ERROR", (await Assert.ThrowsAsync<LedgerException>(() =>
            Scope(sp => sp.GetRequiredService<ILedgerAccounts>().EnsureWalletAccountAsync(Guid.NewGuid(), "XYZ")))).Code);
    }

    [Theory]
    [InlineData("UPDATE ledger.journals SET \"Reason\" = 'rewritten'")]
    [InlineData("UPDATE ledger.journals SET \"Status\" = 'Draft', \"PostedAt\" = NULL")]
    [InlineData("DELETE FROM ledger.journals")]
    [InlineData("UPDATE ledger.entries SET \"AmountMinor\" = 5")]
    [InlineData("DELETE FROM ledger.entries")]
    [InlineData("TRUNCATE ledger.entries, ledger.journals")]
    public async Task Posted_journals_and_entries_cannot_be_mutated_or_deleted(string sql)
    {
        var (wallet, account, control) = await Accounts();
        await Post(Funding(account, control, 100));
        await Database(async db => Assert.Equal("ledger_immutable",
            (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).ConstraintName));
        Assert.Equal(100, (await Balance(wallet)).LedgerBalance);
        Assert.Empty((await Reconcile()).Findings);
    }

    [Fact]
    public async Task Cannot_append_a_new_entry_to_a_posted_journal_or_set_balance_directly()
    {
        var (_, account, control) = await Accounts();
        var posted = await Post(Funding(account, control, 100));
        await Database(async db =>
        {
            Assert.Equal("ledger_immutable", (await Assert.ThrowsAsync<PostgresException>(() =>
                InsertLine(db, posted.Id, account, 100, "Credit"))).ConstraintName);
            Assert.Equal("ledger_projection_owned", (await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ledger.account_balances SET \"BalanceMinor\" = 99 WHERE \"AccountId\" = {account}"))).ConstraintName);
        });
    }

    [Fact]
    public async Task Direct_SQL_approval_updates_projection_atomically_and_rollback_undoes_everything()
    {
        var (wallet, account, control) = await Accounts();
        await Database(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var id = Guid.NewGuid();
            await InsertDraft(db, id); await InsertLine(db, id, control, 500, "Debit"); await InsertLine(db, id, account, 500, "Credit");
            await Approve(db, id);
            Assert.Equal(500, (await db.Balances.AsNoTracking().SingleAsync(x => x.AccountId == account)).BalanceMinor);
            Assert.Equal(0, (await Balance(wallet)).LedgerBalance); // Other connections cannot see uncommitted projection.
            await tx.RollbackAsync();
        });
        Assert.Equal(0, (await Balance(wallet)).LedgerBalance);
        await Database(async db => Assert.Empty(await db.Journals.ToListAsync()));
    }

    [Fact]
    public async Task One_hundred_concurrent_journals_use_real_row_locks_without_lost_updates()
    {
        var (wallet, account, control) = await Accounts();
        await Database(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM ledger.account_balances WHERE \"AccountId\" = {account} FOR UPDATE");
            var tasks = Enumerable.Range(0, 100).Select(_ => Post(Funding(account, control, 100))).ToArray();
            var waiting = await WaitingPosters(db);
            await tx.CommitAsync();
            await Task.WhenAll(tasks);
            Assert.True(waiting >= 8, $"Expected actual PostgreSQL lock contention; observed {waiting}.");
        });
        var result = await Balance(wallet);
        Assert.Equal(10000, result.LedgerBalance);
        Assert.Equal(100, result.Version);
        Assert.Empty((await Reconcile()).Findings);
    }

    [Fact]
    public async Task One_hundred_concurrent_debits_allow_only_ten_and_never_make_wallet_negative()
    {
        var (wallet, account, control) = await Accounts();
        await Post(Funding(account, control, 10000));
        async Task<bool> Withdraw()
        {
            try { await Post(new("TestDebit", Guid.NewGuid().ToString(), "SAR", [new(account, PostingSide.Debit, 1000), new(control, PostingSide.Credit, 1000)], "Concurrent debit")); return true; }
            catch (LedgerException ex) { Assert.Equal("INSUFFICIENT_BALANCE", ex.Code); return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Withdraw()));
        Assert.Equal(10, results.Count(x => x));
        Assert.Equal(0, (await Balance(wallet)).LedgerBalance);
        await Database(async db => Assert.Equal(11, await db.Journals.CountAsync()));
        Assert.Empty((await Reconcile()).Findings);
    }

    [Fact]
    public async Task Direct_SQL_cannot_overdraw_a_wallet()
    {
        var (wallet, account, control) = await Accounts();
        await Database(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var id = Guid.NewGuid();
            await InsertDraft(db, id); await InsertLine(db, id, account, 1, "Debit"); await InsertLine(db, id, control, 1, "Credit");
            Assert.Equal("ledger_insufficient_balance", (await Assert.ThrowsAsync<PostgresException>(() => Approve(db, id))).ConstraintName);
        });
        Assert.Equal(0, (await Balance(wallet)).LedgerBalance);
    }

    [Fact]
    public async Task Opposite_line_order_on_overlapping_accounts_does_not_deadlock()
    {
        var (_, first, control) = await Accounts();
        var (secondWallet, second, _) = await Accounts();
        await Post(Funding(first, control, 10000)); await Post(Funding(second, control, 10000));
        var requests = Enumerable.Range(0, 80).Select(i => new PostJournal("AccountingAdjustment", Guid.NewGuid().ToString(), "SAR",
            i % 2 == 0 ? [new(first, PostingSide.Debit, 1), new(second, PostingSide.Credit, 1)]
                : [new(second, PostingSide.Debit, 1), new(first, PostingSide.Credit, 1)], "Lock ordering test"));
        await Task.WhenAll(requests.Select(Post));
        Assert.Equal(10000, (await Balance(secondWallet)).LedgerBalance);
        Assert.Empty((await Reconcile()).Findings);
    }

    [Fact]
    public async Task Bigint_overflow_is_rejected_without_partial_posting()
    {
        var (wallet, account, control) = await Accounts();
        await Post(Funding(account, control, long.MaxValue));
        Assert.Equal("AMOUNT_OUT_OF_RANGE", (await Assert.ThrowsAsync<LedgerException>(() => Post(Funding(account, control, 1)))).Code);
        Assert.Equal(long.MaxValue, (await Balance(wallet)).LedgerBalance);
        await Database(async db => Assert.Equal(1, await db.Journals.CountAsync()));
    }

    [Fact]
    public async Task Correction_is_a_new_exact_reversal_and_cannot_reverse_the_same_original_twice()
    {
        var (wallet, account, control) = await Accounts();
        var original = await Post(Funding(account, control, 10000));
        var source = Guid.NewGuid().ToString(); var actor = Guid.NewGuid();
        Task<PostedJournal> Reverse(string key) => Scope(sp => sp.GetRequiredService<ILedgerPosting>().ReverseAsync(original.Id, "Reversal", key, "Correction", actor));
        var reversal = await Reverse(source);
        Assert.NotEqual(original.Id, reversal.Id);
        Assert.Equal(reversal, await Reverse(source));
        Assert.Equal("ALREADY_REVERSED", (await Assert.ThrowsAsync<LedgerException>(() => Reverse(Guid.NewGuid().ToString()))).Code);
        Assert.Equal(0, (await Balance(wallet)).LedgerBalance);
        await Database(async db =>
        {
            Assert.Equal(2, await db.Journals.CountAsync());
            Assert.Equal(4, await db.Entries.CountAsync());
            Assert.Equal(original.PostedAt, (await db.Journals.SingleAsync(x => x.Id == original.Id)).PostedAt);
        });
    }

    [Fact]
    public async Task Reconciliation_detects_projection_drift_unbalanced_journals_and_missing_wallet_accounts()
    {
        var (_, account, control) = await Accounts();
        var posted = await Post(Funding(account, control, 10000));
        var missing = new Wallet { CustomerId = Guid.NewGuid(), PublicHandle = Guid.NewGuid().ToString("N") };
        await Scope(async sp => { var db = sp.GetRequiredService<WalletsDbContext>(); db.Wallets.Add(missing); await db.SaveChangesAsync(); return true; });
        // Corruption is deliberately injected by the test migration owner, never by the application role.
        await Database(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ledger.account_balances DISABLE TRIGGER balance_owned");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ledger.account_balances SET \"BalanceMinor\" = 10007 WHERE \"AccountId\" = {account}");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ledger.account_balances ENABLE TRIGGER balance_owned");
            await tx.CommitAsync();
        });
        var report = await Reconcile();
        Assert.Contains(report.Findings, x => x.Kind == "BalanceDrift" && x.ResourceId == account && x.Expected == 10000 && x.Actual == 10007);
        Assert.Contains(report.Findings, x => x.Kind == "WalletWithoutAccount" && x.ResourceId == missing.Id);
        Assert.DoesNotContain(report.Findings, x => x.Kind == "UnbalancedJournal");
        await Database(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ledger.entries DISABLE TRIGGER entry_guard");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ledger.entries SET \"AmountMinor\" = 9999 WHERE \"JournalId\" = {posted.Id} AND \"Side\" = 'Debit'");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ledger.entries ENABLE TRIGGER entry_guard");
            await tx.CommitAsync();
        });
        Assert.Contains((await Reconcile()).Findings, x => x.Kind == "UnbalancedJournal" && x.ResourceId == posted.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wallet_stays_pending_on_failure_and_retry_recovers_including_lost_acknowledgement(bool committed)
    {
        using var client = _factory.CreateClient();
        await Customer(client, "pending@example.test");
        await using var failing = new IdentityPostgresFactory(_connection).WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddScoped<ILedgerAccounts>(sp => new FailingAccounts(sp.GetRequiredService<LedgerStore>(), committed))));
        using var failingClient = failing.CreateClient();
        failingClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        var response = await failingClient.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var pending = (await response.Content.ReadFromJsonAsync<WalletResponse>())!;
        Assert.Equal(WalletStatus.Pending, pending.Status);
        await Database(async db => Assert.Equal(committed ? 1 : 0, await db.Accounts.CountAsync(x => x.WalletId == pending.Id)));
        if (!committed)
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/api/v1/wallets/{pending.Id}/balance")).StatusCode);
        // Background retry entry point, with a fresh scope just like a restarted process.
        Assert.Equal(1, await Scope(sp => sp.GetRequiredService<WalletProvisioning>().RetryBatchAsync()));
        Assert.Equal(0, await Scope(sp => sp.GetRequiredService<WalletProvisioning>().RetryBatchAsync()));
        var active = await client.GetFromJsonAsync<WalletResponse>($"/api/v1/wallets/{pending.Id}");
        Assert.Equal(WalletStatus.Active, active!.Status);
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<WalletsDbContext>();
            Assert.NotNull((await db.Wallets.SingleAsync()).LedgerProvisionedAt);
            Assert.Equal(1, await db.WalletStatusHistory.CountAsync());
            return true;
        });
        Assert.Empty((await Reconcile()).Findings);
    }

    [Fact]
    public async Task Upgrade_backfills_legacy_wallets_without_unfreezing_or_reopening_them()
    {
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<WalletsDbContext>();
            await db.GetService<IMigrator>().MigrateAsync("20260920114634_InitialWallets");
            foreach (var status in new[] { "Active", "Frozen", "Restricted", "Closed" })
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO wallets.wallets ("Id", "CustomerId", "Currency", "Status", "PublicHandle", "CreatedAt", "UpdatedAt", "ConcurrencyVersion")
                    VALUES ({Guid.NewGuid()}, {Guid.NewGuid()}, 'SAR', {status}, {status}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow}, {Guid.NewGuid()})
                    """);
            await db.Database.MigrateAsync();
            Assert.Equal(WalletStatus.Pending, (await db.Wallets.SingleAsync(x => x.PublicHandle == "Active")).Status);
            return true;
        });
        Assert.Equal(4, await Scope(sp => sp.GetRequiredService<WalletProvisioning>().RetryBatchAsync()));
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<WalletsDbContext>();
            var rows = await db.Wallets.ToListAsync();
            Assert.All(rows, x => { Assert.NotNull(x.LedgerProvisionedAt); Assert.Equal(x.PublicHandle, x.Status.ToString()); });
            Assert.False(db.Database.HasPendingModelChanges());
            return true;
        });
        Assert.Empty((await Reconcile()).Findings);
    }

    [Fact]
    public async Task Pending_legacy_wallet_cannot_be_activated_without_a_ledger_account()
    {
        var wallet = new Wallet { CustomerId = Guid.NewGuid(), PublicHandle = Guid.NewGuid().ToString("N"), Status = WalletStatus.Frozen };
        await Scope(async sp => { var db = sp.GetRequiredService<WalletsDbContext>(); db.Wallets.Add(wallet); await db.SaveChangesAsync(); return true; });
        var error = await Assert.ThrowsAsync<WalletRuleException>(() => Scope(sp => sp.GetRequiredService<IWalletService>()
            .UnfreezeAsync(wallet.Id, new("Backfill pending", wallet.ConcurrencyVersion), Guid.NewGuid(), default)));
        Assert.Equal("LEDGER_UNAVAILABLE", error.Code);
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<WalletsDbContext>();
            Assert.Equal("ck_wallet_active_has_ledger", (await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"UPDATE wallets.wallets SET \"Status\" = 'Active' WHERE \"Id\" = {wallet.Id}"))).ConstraintName);
            return true;
        });
    }

    [Fact]
    public async Task Database_enforces_unique_source_and_unique_wallet_currency_without_application_locks()
    {
        var (wallet, account, control) = await Accounts();
        var request = Funding(account, control, 100);
        await Post(request);
        await Database(async db =>
        {
            Assert.Equal("IX_journals_SourceType_SourceId", (await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ledger.journals ("Id", "SourceType", "SourceId", "RequestHash", "Currency", "Reason", "Status")
                    VALUES ({Guid.NewGuid()}, {request.SourceType}, {request.SourceId}, 'different', 'SAR', 'Test duplicate', 'Draft')
                    """))).ConstraintName);
            Assert.Equal("IX_accounts_WalletId_Currency", (await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ledger.accounts ("Id", "WalletId", "Code", "Currency", "Kind", "NormalSide")
                    VALUES ({Guid.NewGuid()}, {wallet}, 'duplicate-wallet-account', 'SAR', 'CustomerWalletLiability', 'Credit')
                    """))).ConstraintName);
        });
    }

    [Fact]
    public async Task Reconciliation_snapshot_does_not_report_false_drift_during_posting()
    {
        var (_, account, control) = await Accounts();
        var posts = Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Post(Funding(account, control, 100))));
        for (var i = 0; i < 10; i++) Assert.Empty((await Reconcile()).Findings);
        await posts;
        Assert.Empty((await Reconcile()).Findings);
    }

    [Fact]
    public async Task Incorrect_reversal_and_reversal_without_funds_are_rejected_atomically()
    {
        var (wallet, account, control) = await Accounts();
        var original = await Post(Funding(account, control, 100));
        Assert.Equal("INVALID_REVERSAL", (await Assert.ThrowsAsync<LedgerException>(() => Post(
            new("Reversal", Guid.NewGuid().ToString(), "SAR", [new(account, PostingSide.Debit, 99), new(control, PostingSide.Credit, 99)],
                "Incorrect compensation", Guid.NewGuid(), original.Id)))).Code);
        await Post(new("Adjustment", Guid.NewGuid().ToString(), "SAR", [new(account, PostingSide.Debit, 100), new(control, PostingSide.Credit, 100)], "Consume funds"));
        Assert.Equal("INSUFFICIENT_BALANCE", (await Assert.ThrowsAsync<LedgerException>(() => Scope(sp => sp.GetRequiredService<ILedgerPosting>()
            .ReverseAsync(original.Id, "Reversal", Guid.NewGuid().ToString(), "No funds", Guid.NewGuid())))).Code);
        Assert.Equal(0, (await Balance(wallet)).LedgerBalance);
        await Database(async db => Assert.Equal(2, await db.Journals.CountAsync()));
    }

    [Fact]
    public async Task Balance_and_demo_funding_endpoints_enforce_permissions_ownership_and_conflict_contract()
    {
        using var owner = _factory.CreateClient(); using var other = _factory.CreateClient(); using var anonymous = _factory.CreateClient();
        var ownerId = await Customer(owner, "balance-owner@example.test");
        await Customer(other, "balance-other@example.test");
        var wallet = (await (await owner.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest())).Content.ReadFromJsonAsync<WalletResponse>())!;
        var path = $"/api/v1/wallets/{wallet.Id}/balance";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path)).StatusCode);
        var funding = new DemoFundsRequest(10000, Guid.NewGuid().ToString(), "Demo funds");
        var fundPath = $"/api/v1/wallets/{wallet.Id}/demo-funds";
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(fundPath, funding)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(fundPath, funding)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(fundPath, funding)).StatusCode);
        var conflict = await owner.PostAsJsonAsync(fundPath, funding with { AmountMinor = 20000 });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", json.RootElement.GetProperty("code").GetString());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _));
        Assert.True(json.RootElement.TryGetProperty("correlationId", out _));
        var balance = await owner.GetFromJsonAsync<WalletBalance>(path);
        Assert.Equal(10000, balance!.LedgerBalance); Assert.Equal(10000, balance.AvailableBalance);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/admin/ledger/reconciliation")).StatusCode);
        await _factory.InDatabaseAsync(async db =>
        {
            await db.UserRoles.Where(x => x.UserId == ownerId).ExecuteDeleteAsync();
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(fundPath, funding)).StatusCode);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    public async Task Demo_endpoint_is_registered_only_in_explicit_nonproduction_environments(string environment, bool expected)
    {
        await using var app = new IdentityPostgresFactory(_connection).WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Jwt:SigningKey"] = ProductionConfigurationPhase12Tests.Safe()["Identity:Jwt:SigningKey"],
                ["Identity:Otp:HashKey"] = ProductionConfigurationPhase12Tests.Safe()["Identity:Otp:HashKey"],
                ["Identity:TokenHashKey"] = ProductionConfigurationPhase12Tests.Safe()["Identity:TokenHashKey"]
            }));
            if (environment == "Production") b.ConfigureAppConfiguration((_, c) =>
            {
                var safe = ProductionConfigurationPhase12Tests.Safe();
                safe["Caching:Enabled"] = "false";
                safe["HttpSecurity:ExternalWorkersDecision"] = "TEST-1200 endpoint registration only; all workers are disabled";
                c.AddInMemoryCollection(safe);
            });
        });
        using var client = app.CreateClient();
        await client.GetAsync("/health");
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        Assert.Equal(expected, endpoints.Any(x => x.RoutePattern.RawText!.EndsWith("/demo-funds", StringComparison.Ordinal)));
    }

    private Task<T> Scope<T>(Func<IServiceProvider, Task<T>> action) => WithScope(_factory.Services, action);
    private static async Task<T> WithScope<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> action)
    { await using var scope = services.CreateAsyncScope(); return await action(scope.ServiceProvider); }
    private async Task Database(Func<LedgerDbContext, Task> action) => await Scope(async sp => { await action(sp.GetRequiredService<LedgerDbContext>()); return true; });
    private Task<PostedJournal> Post(PostJournal request) => Scope(sp => sp.GetRequiredService<ILedgerPosting>().PostAsync(request));
    private async Task<WalletBalance> Balance(Guid wallet) => (await Scope(sp => sp.GetRequiredService<ILedgerReader>().GetWalletBalanceAsync(wallet, "SAR")))!;
    private Task<ReconciliationReport> Reconcile() => Scope(sp => sp.GetRequiredService<ILedgerReconciliation>().RunAsync());
    private async Task<(Guid Wallet, Guid Account, Guid Control)> Accounts()
    {
        var wallet = Guid.NewGuid();
        return await Scope(async sp =>
        {
            var accounts = sp.GetRequiredService<ILedgerAccounts>();
            return (wallet, await accounts.EnsureWalletAccountAsync(wallet, "SAR"),
                await accounts.EnsureSystemAccountAsync(SystemAccount.AdjustmentControl, "SAR"));
        });
    }
    private static PostJournal Funding(Guid account, Guid control, long amount) => new("TestFunding", Guid.NewGuid().ToString(), "SAR",
        [new(control, PostingSide.Debit, amount), new(account, PostingSide.Credit, amount)], "Test funding");
    private static Task InsertDraft(LedgerDbContext db, Guid id, string status = "Draft") => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO ledger.journals ("Id", "SourceType", "SourceId", "RequestHash", "Currency", "Reason", "Status")
        VALUES ({id}, 'DirectSQL', {id.ToString()}, 'test', 'SAR', 'Database boundary test', {status})
        """);
    private static Task InsertLine(LedgerDbContext db, Guid journal, Guid account, long amount, string side) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO ledger.entries ("Id", "JournalId", "AccountId", "AmountMinor", "Side", "Currency")
        VALUES ({Guid.NewGuid()}, {journal}, {account}, {amount}, {side}, 'SAR')
        """);
    private static Task Approve(LedgerDbContext db, Guid journal) => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ledger.journals SET \"Status\" = 'Posted' WHERE \"Id\" = {journal}");
    private static async Task<int> WaitingPosters(LedgerDbContext db)
    {
        var until = DateTime.UtcNow.AddSeconds(15); var count = 0;
        while (DateTime.UtcNow < until && count < 8)
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_stat_clear_snapshot()");
            count = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%ledger.account_balances%'").SingleAsync();
            if (count < 8) await Task.Delay(20);
        }
        return count;
    }
    private async Task<Guid> Customer(HttpClient client, string email)
    {
        var device = new DeviceRequest(email, "Test");
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Password!12345", true, "ar-SA", device));
        using var json = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        var challenge = json.RootElement.GetProperty("challengeId").GetGuid();
        var auth = (await (await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, _factory.Otp.Get(challenge), device))).Content.ReadFromJsonAsync<AuthResult>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        return await Scope(async sp =>
        {
            var identity = sp.GetRequiredService<IdentityDbContext>();
            var id = await identity.RefreshTokens.Where(x => x.FamilyId == auth.SessionId).Select(x => x.UserId).SingleAsync();
            var customers = sp.GetRequiredService<CustomersDbContext>();
            customers.Customers.Add(new Customer { IdentityUserId = id, FirstName = "Test", LastName = "Customer", Status = CustomerStatus.Active, KycStatus = KycStatus.Verified });
            await customers.SaveChangesAsync();
            return id;
        });
    }

    private sealed class FailingAccounts(LedgerStore store, bool commit) : ILedgerAccounts
    {
        public async Task<Guid> EnsureWalletAccountAsync(Guid walletId, string currency, CancellationToken ct = default)
        {
            if (commit) await store.EnsureWalletAccountAsync(walletId, currency, ct);
            throw new IOException(commit ? "Simulated acknowledgement loss after Ledger committed." : "Simulated ledger outage.");
        }
        public Task<Guid> EnsureSystemAccountAsync(SystemAccount account, string currency, CancellationToken ct = default) => store.EnsureSystemAccountAsync(account, currency, ct);
    }
}
