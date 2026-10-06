using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalWallet.Beneficiaries.Domain;
using DigitalWallet.Beneficiaries.Infrastructure;
using DigitalWallet.Customers.Domain;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Customers.Services;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Ledger.Domain;
using DigitalWallet.Ledger.Infrastructure;
using DigitalWallet.RiskLimits.Application;
using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.RiskLimits.Domain;
using DigitalWallet.RiskLimits.Infrastructure;
using DigitalWallet.Transfers;
using DigitalWallet.Transfers.Application;
using DigitalWallet.Transfers.Contracts;
using DigitalWallet.Transfers.Domain;
using DigitalWallet.Transfers.Infrastructure;
using DigitalWallet.Wallets.Domain;
using DigitalWallet.Wallets.Persistence;
using DigitalWallet.Wallets.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Transfer = DigitalWallet.Transfers.Domain.Transfer;

namespace DigitalWallet.Api.Tests;

public sealed partial class TransfersPostgresTests : IClassFixture<IdentityDatabaseFixture>, IClassFixture<RabbitDatabaseFixture>, IAsyncLifetime
{
    private readonly IdentityPostgresFactory _factory;
    private readonly string _connection;
    private readonly RabbitDatabaseFixture _rabbitFixture;
    private readonly List<HttpClient> _clients = [];
    public TransfersPostgresTests(IdentityDatabaseFixture fixture, RabbitDatabaseFixture rabbit)
    {
        _connection = fixture.ConnectionString;
        _rabbitFixture = rabbit;
        _factory = new(_connection, new Dictionary<string, string?>
        {
            ["Messaging:RabbitUri"] = rabbit.ConnectionString,
            ["Messaging:Prefix"] = "phase9." + Guid.NewGuid().ToString("N"),
            ["Messaging:LeaseSeconds"] = "4", ["Messaging:PublishTimeoutSeconds"] = "2",
            ["Messaging:RetryBaseMilliseconds"] = "150"
        });
    }
    public Task InitializeAsync() => _factory.ResetAsync();
    public async Task DisposeAsync()
    {
        try
        {
            // Every acceptance scenario ends with the production reconciliation, including failures and races.
            var report = await Scope(sp => sp.GetRequiredService<ILedgerReconciliation>().RunAsync());
            Assert.Empty(report.Findings);
        }
        finally { foreach (var c in _clients) c.Dispose(); await _factory.DisposeAsync(); }
    }
    private async Task<T> Scope<T>(Func<IServiceProvider, Task<T>> action)
    { await using var scope = _factory.Services.CreateAsyncScope(); return await action(scope.ServiceProvider); }
    private Task<T> Db<T>(Func<TransfersDbContext, Task<T>> action) => Scope(sp => action(sp.GetRequiredService<TransfersDbContext>()));
    private Task<T> Service<T>(Func<TransferService, Task<T>> action) => Scope(sp => action(sp.GetRequiredService<TransferService>()));
    private async Task<Actor> ActorAsync(string name)
    {
        var client = _factory.CreateClient(); _clients.Add(client);
        var email = name + "@phase7.test";
        var device = new DeviceRequest(name, "Phase7");
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Password!12345", true, "ar-SA", device));
        var challenge = await Read<Challenge>(reg, HttpStatusCode.Accepted);
        var auth = await Read<AuthResult>(await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge.ChallengeId, _factory.Otp.Get(challenge.ChallengeId), device)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        var identity = Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken).Subject);
        var customer = await Read<CustomerResponse>(await client.PostAsJsonAsync("/api/v1/me/profile", new CreateProfileRequest(name, "PrivateLastname", null, "ar-SA")), HttpStatusCode.Created);
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<CustomersDbContext>(); var c = await db.Customers.SingleAsync(x => x.Id == customer.Id);
            c.Status = CustomerStatus.Active; c.KycStatus = KycStatus.Verified; await db.SaveChangesAsync(); return true;
        });
        var wallet = await Read<WalletResponse>(await client.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest()), HttpStatusCode.Created);
        return new(client, identity, customer.Id, wallet.Id, email);
    }
    private async Task<Guid> Beneficiary(Actor sender, Actor receiver)
    {
        return await Scope(async sp =>
        {
            var b = DigitalWallet.Beneficiaries.Domain.Beneficiary.Create(sender.CustomerId, receiver.WalletId, "Private nickname", "R*******", _factory.Time.GetUtcNow());
            b.Verify(_factory.Time.GetUtcNow(), TimeSpan.Zero); b.Activate(_factory.Time.GetUtcNow());
            var db = sp.GetRequiredService<BeneficiariesDbContext>(); db.Beneficiaries.Add(b); await db.SaveChangesAsync(); return b.Id;
        });
    }
    private async Task Fund(Actor actor, long amount)
    {
        await Scope(async sp =>
        {
            var accounts = sp.GetRequiredService<ILedgerAccounts>();
            var wallet = await accounts.EnsureWalletAccountAsync(actor.WalletId, "SAR");
            var control = await accounts.EnsureSystemAccountAsync(SystemAccount.AdjustmentControl, "SAR");
            return await sp.GetRequiredService<ILedgerPosting>().PostAsync(new("Phase7Funding", Guid.NewGuid().ToString(), "SAR",
                [new(control, PostingSide.Debit, amount), new(wallet, PostingSide.Credit, amount)], "Test funding"));
        });
    }
    private CreateRule Definition(RuleKind kind, RuleScope scope = RuleScope.System, string key = "*") =>
        new(kind, scope, key, 1, "SAR", "Internal", _factory.Time.GetUtcNow().AddDays(-1), null, "Phase 7 acceptance",
            MaximumTransferMinor: kind == RuleKind.Limit ? long.MaxValue : null,
            FixedFeeMinor: kind == RuleKind.Fee ? 0 : null, BasisPoints: kind == RuleKind.Fee ? 0 : null);
    private async Task Publish(CreateRule rule) => await Scope(async sp =>
    {
        var admin = sp.GetRequiredService<IRuleAdministration>(); var actor = Guid.NewGuid();
        return await admin.PublishAsync((await admin.CreateAsync(rule, actor)).Id, actor);
    });
    private async Task<Setup> SetupAsync(long balance = 10000, long fee = 0, CreateRule? limit = null, CreateRule? risk = null)
    {
        await Publish(limit ?? Definition(RuleKind.Limit));
        await Publish(Definition(RuleKind.Fee) with { FixedFeeMinor = fee });
        await Publish(risk ?? Definition(RuleKind.Risk));
        var a = await ActorAsync("Sender"); var b = await ActorAsync("Recipient");
        await Fund(a, balance); return new(a, b, await Beneficiary(a, b));
    }
    private async Task<QuoteResponse> Quote(Setup s, long amount = 1000) => await Read<QuoteResponse>(await s.A.Client.PostAsJsonAsync("/api/v1/transfers/quotes", new QuoteRequest(s.BeneficiaryId, amount, "SAR")));
    private Task<HttpResponseMessage> CreateResponse(Actor a, Guid quote, string key, string? note = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers") { Content = JsonContent.Create(new CreateTransferRequest(quote, note)) };
        message.Headers.Add("Idempotency-Key", key); return a.Client.SendAsync(message);
    }
    private async Task<TransferReceipt> Intent(Setup s, long amount = 1000, string? key = null)
    { var q = await Quote(s, amount); return await Read<TransferReceipt>(await CreateResponse(s.A, q.Id, key ?? Guid.NewGuid().ToString()), HttpStatusCode.Created); }
    private Task<Transfer> Stored(Guid id) => Db(db => db.Transfers.AsNoTracking().SingleAsync(x => x.Id == id));
    private async Task<string> Code(Guid id) => _factory.Otp.Get((await Stored(id)).OtpChallengeId!.Value);
    private async Task<HttpResponseMessage> Confirm(Actor a, Guid id, string? code = null) => await a.Client.PostAsJsonAsync($"/api/v1/transfers/{id}/confirm", new ConfirmTransferRequest(code ?? await Code(id)));
    private Task<long> Balance(Actor actor) => Scope(async sp => (await sp.GetRequiredService<ILedgerReader>().GetWalletBalanceAsync(actor.WalletId, "SAR"))!.LedgerBalance);
    private Task<int> Journals() => Scope(sp => sp.GetRequiredService<LedgerDbContext>().Journals.CountAsync(x => x.SourceType == "InternalTransfer"));
    private async Task NoFinancialEffect(Guid id, long balance, Actor sender)
    {
        var t = await Stored(id); Assert.Equal(TransferStatus.PendingConfirmation, t.Status); Assert.Null(t.JournalId); Assert.Null(t.CompletedAt); Assert.Null(t.ConfirmedAt);
        Assert.Null(await Db(db => db.Quotes.Where(x => x.Id == t.QuoteId).Select(x => x.UsedAt).SingleAsync()));
        Assert.Equal(0, await Journals()); Assert.Equal(balance, await Balance(sender));
        Assert.Equal(0, await Scope(sp => sp.GetRequiredService<RiskLimitsDbContext>().Usage.CountAsync()));
        Assert.Equal(0, await Scope(sp => sp.GetRequiredService<RiskLimitsDbContext>().Operations.CountAsync()));
        var receipt = await Db(db => db.Idempotency.Where(x => x.TransferId == id).Select(x => x.ResponseJson).SingleAsync());
        Assert.Equal("PendingConfirmation", JsonSerializer.Deserialize<TransferReceipt>(receipt)!.Status);
    }
    private static async Task<T> Read<T>(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    { Assert.True(r.StatusCode == expected, $"Expected {expected}, got {r.StatusCode}: {await r.Content.ReadAsStringAsync()}"); return (await r.Content.ReadFromJsonAsync<T>())!; }
    private static async Task Error(HttpResponseMessage r, string code, HttpStatusCode? status = null)
    {
        Assert.False(r.IsSuccessStatusCode); if (status != null) Assert.Equal(status, r.StatusCode);
        using var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _)); Assert.True(json.RootElement.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Migrations_from_empty_database_have_no_pending_model_changes()
    {
        await _factory.Services.InitializeTransfersModuleAsync();
        await Db(async db =>
        {
            Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains("20260922155518_InitialTransfers", await db.Database.GetAppliedMigrationsAsync());
            Assert.All(db.Model.GetEntityTypes(), x => Assert.Equal("transfers", x.GetSchema()));
            return true;
        });
    }
    [Fact]
    public async Task Quote_and_intent_are_server_derived_and_have_no_financial_effect()
    {
        var s = await SetupAsync(fee: 25); var q = await Quote(s);
        Assert.Equal(25, q.FeeMinor); Assert.Equal(1025, q.TotalDebitMinor); Assert.Equal("Allow", q.RiskDecision);
        var stored = await Db(db => db.Quotes.SingleAsync());
        Assert.Equal(s.A.CustomerId, stored.OwnerCustomerId); Assert.Equal(s.A.WalletId, stored.SenderWalletId); Assert.Equal(s.B.WalletId, stored.TargetWalletId);
        Assert.NotEmpty(stored.RequestFingerprint); Assert.NotEmpty(stored.LimitRuleVersionsJson); Assert.NotEmpty(stored.RiskRuleVersionsJson);
        Assert.Equal(_factory.Time.GetUtcNow().AddSeconds(300), q.ExpiresAt);
        var response = await CreateResponse(s.A, q.Id, "intent"); var raw = await response.Content.ReadAsStringAsync();
        var t = await Read<TransferReceipt>(response, HttpStatusCode.Created);
        Assert.DoesNotContain("challengeId", raw, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("Private nickname", raw); Assert.DoesNotContain("PrivateLastname", raw);
        await NoFinancialEffect(t.Id, 10000, s.A);
    }
    [Theory]
    [InlineData(0, "SAR")]
    [InlineData(-1, "SAR")]
    [InlineData(100, "USD")]
    [InlineData(100, "sar")]
    public async Task Quote_rejects_invalid_amount_currency(long amount, string currency)
    {
        var s = await SetupAsync(); await Error(await s.A.Client.PostAsJsonAsync("/api/v1/transfers/quotes", new QuoteRequest(s.BeneficiaryId, amount, currency)), "VALIDATION_ERROR");
        Assert.Equal(0, await Db(db => db.Quotes.CountAsync()));
    }
    [Fact]
    public async Task Expired_and_foreign_quotes_cannot_create_an_intent()
    {
        var s = await SetupAsync(); var q = await Quote(s);
        await Error(await CreateResponse(s.B, q.Id, "foreign"), "QUOTE_NOT_FOUND", HttpStatusCode.NotFound);
        _factory.Time.Advance(TimeSpan.FromSeconds(300));
        await Error(await CreateResponse(s.A, q.Id, "expired"), "QUOTE_EXPIRED");
        Assert.Equal(0, await Db(db => db.Transfers.CountAsync()));
    }
    [Fact]
    public async Task Used_quote_and_same_quote_under_another_key_are_rejected()
    {
        var s = await SetupAsync(); var t = await Intent(s); var q = (await Stored(t.Id)).QuoteId;
        await Error(await CreateResponse(s.A, q, "second"), "QUOTE_USED");
        await Read<TransferReceipt>(await Confirm(s.A, t.Id));
        await Error(await CreateResponse(s.A, q, "third"), "QUOTE_USED");
        Assert.Equal(1, await Db(db => db.Transfers.CountAsync())); Assert.Equal(1, await Journals());
    }
    [Theory]
    [InlineData("fee")]
    [InlineData("limit")]
    [InlineData("risk")]
    [InlineData("fee-same-value")]
    public async Task Changed_fee_or_rule_versions_require_a_new_quote(string kind)
    {
        var s = await SetupAsync(); var t = await Intent(s);
        var rule = Definition(kind.StartsWith("fee") ? RuleKind.Fee : kind == "limit" ? RuleKind.Limit : RuleKind.Risk, RuleScope.Customer, s.A.CustomerId.ToString());
        if (kind == "fee") rule = rule with { FixedFeeMinor = 20 };
        if (kind == "risk") rule = rule with { ReviewFromMinor = 1 };
        await Publish(rule);
        await Error(await Confirm(s.A, t.Id), "QUOTE_STALE", HttpStatusCode.Conflict);
        await NoFinancialEffect(t.Id, 10000, s.A);
    }
    [Theory]
    [InlineData("note")]
    [InlineData("quote")]
    public async Task Same_key_with_changed_request_is_a_conflict(string field)
    {
        var s = await SetupAsync(); var q = await Quote(s);
        var t = await Read<TransferReceipt>(await CreateResponse(s.A, q.Id, "key", "a"), HttpStatusCode.Created);
        var other = field == "quote" ? await Quote(s) : q;
        await Error(await CreateResponse(s.A, other.Id, "key", field == "note" ? "b" : "a"), "IDEMPOTENCY_KEY_REUSED", HttpStatusCode.Conflict);
        Assert.Equal(1, await Db(db => db.Transfers.CountAsync())); await NoFinancialEffect(t.Id, 10000, s.A);
    }
    [Fact]
    public async Task Idempotency_key_is_required_and_scoped_to_owner()
    {
        var s = await SetupAsync(); var q = await Quote(s);
        await Error(await s.A.Client.PostAsJsonAsync("/api/v1/transfers", new CreateTransferRequest(q.Id, null)), "IDEMPOTENCY_KEY_REQUIRED");
        var first = await Read<TransferReceipt>(await CreateResponse(s.A, q.Id, "shared"), HttpStatusCode.Created);
        await Fund(s.B, 10000); var reverse = new Setup(s.B, s.A, await Beneficiary(s.B, s.A));
        var second = await Intent(reverse, key: "shared"); Assert.NotEqual(first.Id, second.Id);
    }
    [Fact]
    public async Task One_hundred_identical_HTTP_requests_create_one_transfer_and_one_journal()
    {
        var s = await SetupAsync(); var q = await Quote(s);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ => await Read<TransferReceipt>(await CreateResponse(s.A, q.Id, "100-identical"), HttpStatusCode.Created)));
        var id = results[0].Id; Assert.All(results, x => Assert.Equal(id, x.Id));
        var code = await Code(id);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ => await Read<TransferReceipt>(await Confirm(s.A, id, code))));
        Assert.All(receipts, x => Assert.Equal(receipts[0], x)); Assert.Equal("Completed", receipts[0].Status);
        Assert.Equal(1, await Db(db => db.Transfers.CountAsync())); Assert.Equal(1, await Db(db => db.Idempotency.CountAsync()));
        Assert.Equal(1, await Journals()); Assert.Equal(9000, await Balance(s.A)); Assert.Equal(1000, await Balance(s.B));
        Assert.Equal(1, await Scope(sp => sp.GetRequiredService<RiskLimitsDbContext>().Operations.CountAsync()));
    }
    [Fact]
    public async Task One_hundred_distinct_10_SAR_debits_from_100_SAR_complete_exactly_ten()
    {
        var s = await SetupAsync();
        var intents = new List<TransferReceipt>();
        for (var i = 0; i < 100; i++) intents.Add(await Intent(s));
        var responses = await Task.WhenAll(intents.Select(t => Confirm(s.A, t.Id)));
        Assert.Equal(10, responses.Count(x => x.IsSuccessStatusCode));
        foreach (var r in responses.Where(x => !x.IsSuccessStatusCode)) await Error(r, "INSUFFICIENT_BALANCE");
        Assert.Equal(10, await Journals()); Assert.Equal(0, await Balance(s.A)); Assert.Equal(10000, await Balance(s.B));
        Assert.Equal(10, await Db(db => db.Transfers.CountAsync(x => x.Status == TransferStatus.Completed)));
        Assert.Equal(10, await Db(db => db.Quotes.CountAsync(x => x.UsedAt != null)));
        Assert.Equal(10, await Scope(sp => sp.GetRequiredService<RiskLimitsDbContext>().Operations.CountAsync(x => x.Consumed)));
        Assert.False(await Scope(sp => sp.GetRequiredService<LedgerDbContext>().Balances.AnyAsync(x => x.BalanceMinor < 0)));
    }
    [Fact]
    public async Task Opposite_directions_share_UUID_lock_order_and_conserve_value()
    {
        var s = await SetupAsync(); await Fund(s.B, 10000); var reverse = new Setup(s.B, s.A, await Beneficiary(s.B, s.A));
        var outgoing = new List<(Actor Actor, TransferReceipt Transfer)>();
        for (var i = 0; i < 20; i++) { outgoing.Add((s.A, await Intent(s, 100))); outgoing.Add((s.B, await Intent(reverse, 100))); }
        var responses = await Task.WhenAll(outgoing.Select(x => Confirm(x.Actor, x.Transfer.Id)));
        foreach (var r in responses) Assert.Equal("Completed", (await Read<TransferReceipt>(r)).Status);
        Assert.Equal(40, await Journals()); Assert.Equal(10000, await Balance(s.A)); Assert.Equal(10000, await Balance(s.B));
    }
    [Theory]
    [InlineData("daily")]
    [InlineData("monthly")]
    public async Task Concurrent_transfers_cannot_exceed_period_limits(string period)
    {
        var limit = period == "daily" ? Definition(RuleKind.Limit) with { DailyAmountMinor = 1000 } : Definition(RuleKind.Limit) with { MonthlyAmountMinor = 1000 };
        var s = await SetupAsync(limit: limit); var a = await Intent(s); var b = await Intent(s);
        var results = await Task.WhenAll(Confirm(s.A, a.Id), Confirm(s.A, b.Id));
        Assert.Single(results, r => r.IsSuccessStatusCode);
        await Error(results.Single(r => !r.IsSuccessStatusCode), "QUOTE_STALE");
        Assert.Equal(1, await Journals()); Assert.Equal(9000, await Balance(s.A));
        var q = await Quote(s); Assert.Contains(period == "daily" ? "DAILY_LIMIT_EXCEEDED" : "MONTHLY_LIMIT_EXCEEDED", q.ReasonCodes);
        var rejected = await Read<TransferReceipt>(await CreateResponse(s.A, q.Id, "limit-deny"), HttpStatusCode.Created);
        Assert.Equal("Rejected", (await Read<TransferReceipt>(await Confirm(s.A, rejected.Id))).Status);
        Assert.Equal(1, await Scope(sp => sp.GetRequiredService<RiskLimitsDbContext>().Operations.CountAsync()));
    }
    [Fact]
    public async Task Balance_must_cover_amount_plus_fee_at_quote_and_under_posting_lock()
    {
        var s = await SetupAsync(balance: 1000, fee: 1);
        await Error(await s.A.Client.PostAsJsonAsync("/api/v1/transfers/quotes", new QuoteRequest(s.BeneficiaryId, 1000, "SAR")), "INSUFFICIENT_BALANCE");
        await Fund(s.A, 1); var t = await Intent(s);
        // A concurrent unrelated ledger debit makes the previously affordable quote unaffordable.
        await Scope(async sp =>
        {
            var accounts = sp.GetRequiredService<ILedgerAccounts>();
            return await sp.GetRequiredService<ILedgerPosting>().PostAsync(new("TestSpend", "fee", "SAR",
                [new(await accounts.EnsureWalletAccountAsync(s.A.WalletId, "SAR"), PostingSide.Debit, 1), new(await accounts.EnsureSystemAccountAsync(SystemAccount.AdjustmentControl, "SAR"), PostingSide.Credit, 1)], "Test spend"));
        });
        await Error(await Confirm(s.A, t.Id), "INSUFFICIENT_BALANCE"); await NoFinancialEffect(t.Id, 1000, s.A);
    }
    [Theory]
    [InlineData("AfterOtp")]
    [InlineData("AfterUsage")]
    [InlineData("AfterLedger")]
    [InlineData("BeforeCommit")]
    public async Task Failure_windows_roll_back_all_financial_state_and_OTP_receipt_recovers(string point)
    {
        var s = await SetupAsync(fee: 10); var t = await Intent(s);
        _factory.TransferFaults.Fail(point);
        Assert.Equal(HttpStatusCode.InternalServerError, (await Confirm(s.A, t.Id)).StatusCode);
        await NoFinancialEffect(t.Id, 10000, s.A);
        var challenge = (await Stored(t.Id)).OtpChallengeId!.Value;
        Assert.NotNull(await Scope(sp => sp.GetRequiredService<IdentityDbContext>().OtpChallenges.Where(x => x.Id == challenge).Select(x => x.UsedAt).SingleAsync()));
        Assert.Equal("Completed", (await Read<TransferReceipt>(await Confirm(s.A, t.Id))).Status);
        Assert.Equal(1, await Journals()); Assert.Equal(8990, await Balance(s.A));
    }
    [Fact]
    public async Task Lost_commit_response_replays_exact_receipt_from_confirm_and_create()
    {
        var s = await SetupAsync(); var t = await Intent(s, key: "lost-commit"); var stored = await Stored(t.Id);
        _factory.TransferFaults.Fail("AfterCommit");
        Assert.Equal(HttpStatusCode.InternalServerError, (await Confirm(s.A, t.Id)).StatusCode);
        Assert.Equal(TransferStatus.Completed, (await Stored(t.Id)).Status);
        var confirm = await Read<TransferReceipt>(await Confirm(s.A, t.Id));
        var create = await Read<TransferReceipt>(await CreateResponse(s.A, stored.QuoteId, "lost-commit"), HttpStatusCode.Created);
        Assert.Equal(confirm, create); Assert.Equal(1, await Journals());
    }
    [Theory]
    [InlineData("AfterUsage")]
    [InlineData("AfterLedger")]
    public async Task Serialization_failure_retries_the_entire_shared_transaction(string point)
    {
        var s = await SetupAsync(); var t = await Intent(s);
        _factory.TransferFaults.Fail(point, serialization: true);
        Assert.Equal("Completed", (await Read<TransferReceipt>(await Confirm(s.A, t.Id))).Status);
        Assert.Equal(1, await Journals()); Assert.Equal(9000, await Balance(s.A));
        Assert.Equal(1, await Scope(sp => sp.GetRequiredService<RiskLimitsDbContext>().Operations.CountAsync()));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_and_deny_preserve_money_and_do_not_consume_limits(bool deny)
    {
        var rule = deny ? Definition(RuleKind.Risk) with { DenyFromMinor = 1 } : Definition(RuleKind.Risk) with { ReviewFromMinor = 1 };
        var s = await SetupAsync(risk: rule); var t = await Intent(s);
        var receipt = await Read<TransferReceipt>(await Confirm(s.A, t.Id));
        Assert.Equal(deny ? "Rejected" : "PendingReview", receipt.Status); Assert.Equal(deny ? "RISK_REJECTED" : "RISK_REVIEW_REQUIRED", receipt.ReasonCode);
        Assert.Equal(0, await Journals()); Assert.Equal(10000, await Balance(s.A));
        Assert.Equal(0, await Scope(sp => sp.GetRequiredService<RiskLimitsDbContext>().Operations.CountAsync()));
        Assert.Equal(0, await Scope(sp => sp.GetRequiredService<RiskLimitsDbContext>().Usage.CountAsync()));
        Assert.Null(await Db(db => db.Quotes.Select(x => x.UsedAt).SingleAsync()));
    }
    [Theory]
    [InlineData(0, 2)]
    [InlineData(25, 3)]
    public async Task Journal_has_exact_balanced_liability_and_revenue_entries(long fee, int count)
    {
        var s = await SetupAsync(fee: fee); var t = await Intent(s); await Read<TransferReceipt>(await Confirm(s.A, t.Id));
        var stored = await Stored(t.Id); Assert.NotNull(stored.JournalId); Assert.NotNull(stored.CompletedAt);
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<LedgerDbContext>(); var lines = await db.Entries.Where(x => x.JournalId == stored.JournalId).ToListAsync();
            Assert.Equal(count, lines.Count); Assert.All(lines, x => Assert.True(x.AmountMinor > 0));
            Assert.Equal(lines.Where(x => x.Side == EntrySide.Debit).Sum(x => x.AmountMinor), lines.Where(x => x.Side == EntrySide.Credit).Sum(x => x.AmountMinor));
            foreach (var line in lines)
            {
                var account = await db.Accounts.SingleAsync(x => x.Id == line.AccountId);
                if (account.WalletId == s.A.WalletId) { Assert.Equal(EntrySide.Debit, line.Side); Assert.Equal(1000 + fee, line.AmountMinor); }
                else if (account.WalletId == s.B.WalletId) { Assert.Equal(EntrySide.Credit, line.Side); Assert.Equal(1000, line.AmountMinor); }
                else { Assert.Equal(AccountKind.PlatformFeeRevenue, account.Kind); Assert.Equal(EntrySide.Credit, line.Side); Assert.Equal(fee, line.AmountMinor); }
            }
            return true;
        });
    }

    private sealed record Challenge(Guid ChallengeId);
    private sealed record Actor(HttpClient Client, Guid IdentityId, Guid CustomerId, Guid WalletId, string Email);
    private sealed record Setup(Actor A, Actor B, Guid BeneficiaryId);
}
