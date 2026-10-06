using System.Net;
using System.Net.Http.Json;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Ledger.Domain;
using DigitalWallet.Ledger.Infrastructure;
using DigitalWallet.RiskLimits.Infrastructure;
using DigitalWallet.Transactions;
using DigitalWallet.Transactions.Application;
using DigitalWallet.Transactions.Infrastructure;
using DigitalWallet.Transfers.Contracts;
using DigitalWallet.Transfers.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DigitalWallet.Api.Tests;

// Shares the phase-seven financial setup and production reconciliation teardown.
public sealed partial class TransfersPostgresTests
{
    private Task<T> HistoryDb<T>(Func<TransactionsDbContext, Task<T>> action) => Scope(sp => action(sp.GetRequiredService<TransactionsDbContext>()));
    private async Task Grant(Actor actor, params string[] permissions)
    {
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<IdentityDbContext>();
            var id = Guid.NewGuid();
            db.Roles.Add(new ApplicationRole { Id = id, Name = id.ToString(), NormalizedName = id.ToString().ToUpperInvariant() });
            db.UserRoles.Add(new ApplicationUserRole { RoleId = id, UserId = actor.IdentityId });
            foreach (var permission in permissions) db.RolePermissions.Add(new RolePermission { RoleId = id, PermissionCode = permission });
            await db.SaveChangesAsync(); return true;
        });
    }
    private async Task<TransferReceipt> Complete(Setup s, long amount = 1000)
    { var t = await Intent(s, amount); return await Read<TransferReceipt>(await Confirm(s.A, t.Id)); }
    private async Task<HttpResponseMessage> AdminPost(Actor actor, string url, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await actor.Client.SendAsync(request);
    }
    private async Task<ReversalReceipt> RequestReverse(Actor maker, Guid id, string key = "request", string reason = "Customer correction") =>
        await Read<ReversalReceipt>(await AdminPost(maker, $"/api/v1/admin/transfers/{id}/reversal-requests", new RequestReversal(reason), key), HttpStatusCode.Created);
    private async Task<ApproveReversal> Proof(Actor checker, Guid requestId)
    {
        var otp = await Read<OtpChallengeIssued>(await checker.Client.PostAsync($"/api/v1/admin/reversal-requests/{requestId}/otp", null));
        return new("Approved after investigation", otp.ChallengeId, _factory.Otp.Get(otp.ChallengeId));
    }
    private Task<HttpResponseMessage> Approve(Actor checker, Guid id, ApproveReversal proof, string key = "approve") =>
        AdminPost(checker, $"/api/v1/admin/reversal-requests/{id}/approve", proof, key);
    private async Task<(Setup Setup, TransferReceipt Transfer, Actor Maker, Actor Checker, ReversalReceipt Request)> Workflow(long fee = 0)
    {
        var s = await SetupAsync(fee: fee); var t = await Complete(s);
        var maker = await ActorAsync("Maker"); var checker = await ActorAsync("Checker");
        await Grant(maker, "transaction.reverse.request"); await Grant(checker, "transaction.reverse.approve");
        return (s, t, maker, checker, await RequestReverse(maker, t.Id));
    }
    private Task<WalletBalance?> HistoryBalance(Actor a) => Scope(sp => sp.GetRequiredService<ILedgerReader>().GetWalletBalanceAsync(a.WalletId, "SAR"));

    [Fact]
    public async Task Phase8_migrations_and_backfill_are_idempotent_and_have_no_pending_model_changes()
    {
        var s = await SetupAsync(); var pending = await Intent(s); var t = await Complete(s);
        await HistoryDb(async db =>
        {
            Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.All(db.Model.GetEntityTypes(), x => Assert.Equal("transactions", x.GetSchema()));
            Assert.Equal(1, await db.Transactions.CountAsync());
            // Simulate an upgrade from phase seven, which already contains completed transfers.
            await db.Database.ExecuteSqlRawAsync("BEGIN; ALTER TABLE transactions.transactions DISABLE TRIGGER USER; ALTER TABLE transactions.reversal_requests DISABLE TRIGGER USER; TRUNCATE transactions.reversal_requests, transactions.transactions; ALTER TABLE transactions.transactions ENABLE TRIGGER USER; ALTER TABLE transactions.reversal_requests ENABLE TRIGGER USER; COMMIT;");
            return true;
        });
        await _factory.Services.InitializeTransactionsModuleAsync(); await _factory.Services.InitializeTransactionsModuleAsync();
        Assert.Equal(t.Id, await HistoryDb(db => db.Transactions.Select(x => x.Id).SingleAsync()));
        Assert.Equal(TransferStatus.PendingConfirmation, (await Stored(pending.Id)).Status);
    }
    [Fact]
    public async Task History_is_participant_scoped_masked_and_shows_direction_and_fee_effect()
    {
        var s = await SetupAsync(fee: 25); var t = await Complete(s); var outsider = await ActorAsync("Outsider");
        var outgoing = await Read<TransactionReceipt>(await s.A.Client.GetAsync($"/api/v1/transactions/{t.Id}"));
        var response = await s.B.Client.GetAsync($"/api/v1/transactions/{t.Id}"); var raw = await response.Content.ReadAsStringAsync();
        var incoming = await Read<TransactionReceipt>(response);
        Assert.Equal("Outgoing", outgoing.Direction); Assert.Equal(-1025, outgoing.BalanceChangeMinor);
        Assert.Equal("Incoming", incoming.Direction); Assert.Equal(1000, incoming.BalanceChangeMinor);
        Assert.Equal(25, outgoing.FeeMinor); Assert.Equal(t.Id, outgoing.TransferId);
        foreach (var forbidden in new[] { "journalId", "walletId", "customerId", "challengeId", "PrivateLastname", "Private nickname" })
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        await Error(await outsider.Client.GetAsync($"/api/v1/transactions/{t.Id}"), "TRANSACTION_NOT_FOUND", HttpStatusCode.NotFound);
        Assert.Empty((await Read<TransactionPage>(await outsider.Client.GetAsync("/api/v1/transactions"))).Items);
        Assert.Single((await Read<TransactionPage>(await s.A.Client.GetAsync("/api/v1/transactions"))).Items);
    }
    [Fact]
    public async Task Transaction_permissions_are_independent_of_transfer_permissions()
    {
        var s = await SetupAsync(); var t = await Complete(s); var staff = await ActorAsync("Reader");
        await Grant(staff, "transfer.view.any");
        await Error(await staff.Client.GetAsync($"/api/v1/transactions/{t.Id}"), "TRANSACTION_NOT_FOUND", HttpStatusCode.NotFound);
        await Grant(staff, "transaction.view.any");
        var receipt = await Read<TransactionReceipt>(await staff.Client.GetAsync($"/api/v1/transactions/{t.Id}")); Assert.Null(receipt.Direction);
        Assert.Single((await Read<TransactionPage>(await staff.Client.GetAsync("/api/v1/transactions"))).Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await AdminPost(staff, $"/api/v1/admin/transfers/{t.Id}/reversal-requests", new RequestReversal("Reason"), "x")).StatusCode);
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<IdentityDbContext>(); var role = await db.Roles.SingleAsync(x => x.NormalizedName == "CUSTOMER");
            db.RolePermissions.Remove(await db.RolePermissions.SingleAsync(x => x.RoleId == role.Id && x.PermissionCode == "transaction.view.own"));
            await db.SaveChangesAsync(); return true;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await s.A.Client.GetAsync("/api/v1/transactions")).StatusCode);
    }
    [Theory]
    [InlineData("pageSize=0", "VALIDATION_ERROR")]
    [InlineData("pageSize=101", "VALIDATION_ERROR")]
    [InlineData("direction=Sideways", "VALIDATION_ERROR")]
    [InlineData("status=Pending", "VALIDATION_ERROR")]
    [InlineData("type=External", "VALIDATION_ERROR")]
    [InlineData("minAmountMinor=-1", "VALIDATION_ERROR")]
    [InlineData("minAmountMinor=20&maxAmountMinor=10", "VALIDATION_ERROR")]
    [InlineData("cursor=bad", "INVALID_CURSOR")]
    [InlineData("from=2020-01-01T00:00:00Z&to=2026-01-01T00:00:00Z", "DATE_RANGE_TOO_LARGE")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2025-01-01T00:00:00Z", "VALIDATION_ERROR")]
    [InlineData("to=0001-01-01T00:00:00Z", "VALIDATION_ERROR")]
    [InlineData("from=2026-01-01T03:00:00%2B03:00&to=2026-01-02T00:00:00Z", "VALIDATION_ERROR")]
    public async Task History_rejects_invalid_filters(string query, string error)
    { var s = await SetupAsync(); await Error(await s.A.Client.GetAsync("/api/v1/transactions?" + query), error, HttpStatusCode.BadRequest); }
    [Fact]
    public async Task History_keyset_handles_ties_newer_inserts_filters_and_cursor_scope()
    {
        var s = await SetupAsync(); var ids = new List<Guid>();
        for (var i = 0; i < 4; i++) ids.Add((await Complete(s, 100 + i)).Id);
        var page = await Read<TransactionPage>(await s.A.Client.GetAsync("/api/v1/transactions?pageSize=2&type=InternalTransfer&direction=Outgoing"));
        Assert.NotNull(page.NextCursor);
        _factory.Time.Advance(TimeSpan.FromSeconds(61)); var newer = await Complete(s, 200);
        var next = await Read<TransactionPage>(await s.A.Client.GetAsync("/api/v1/transactions?pageSize=2&type=InternalTransfer&direction=Outgoing&cursor=" + Uri.EscapeDataString(page.NextCursor!)));
        Assert.Null(next.NextCursor); Assert.Equal(ids.Order(), page.Items.Concat(next.Items).Select(x => x.Id).Order());
        await Error(await s.B.Client.GetAsync("/api/v1/transactions?pageSize=2&type=InternalTransfer&direction=Outgoing&cursor=" + Uri.EscapeDataString(page.NextCursor!)), "INVALID_CURSOR", HttpStatusCode.BadRequest);
        await Error(await s.A.Client.GetAsync("/api/v1/transactions?pageSize=2&type=Reversal&direction=Outgoing&cursor=" + Uri.EscapeDataString(page.NextCursor!)), "INVALID_CURSOR", HttpStatusCode.BadRequest);
        var filtered = await Read<TransactionPage>(await s.A.Client.GetAsync("/api/v1/transactions?minAmountMinor=101&maxAmountMinor=102&direction=Outgoing&status=Completed")); Assert.Equal(2, filtered.Items.Count);
        Assert.Empty((await Read<TransactionPage>(await s.A.Client.GetAsync("/api/v1/transactions?direction=Incoming"))).Items);
        Assert.Single((await Read<TransactionPage>(await s.A.Client.GetAsync("/api/v1/transactions?reference=" + newer.PublicReference))).Items);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public async Task Full_reversal_exactly_compensates_original_including_fees_and_keeps_usage(long fee)
    {
        var w = await Workflow(fee); var proof = await Proof(w.Checker, w.Request.Id);
        var receipt = await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof)); Assert.Equal("Completed", receipt.Status);
        Assert.Equal(10000, (await HistoryBalance(w.Setup.A))!.LedgerBalance); Assert.Equal(0, (await HistoryBalance(w.Setup.B))!.LedgerBalance);
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<LedgerDbContext>(); var original = (await Stored(w.Transfer.Id)).JournalId!.Value;
            var journal = await db.Journals.SingleAsync(x => x.ReversalOf == original);
            Assert.Equal(w.Checker.IdentityId, journal.ActorId); Assert.Equal("Customer correction", journal.Reason);
            var before = await db.Entries.Where(x => x.JournalId == original).ToListAsync(); var after = await db.Entries.Where(x => x.JournalId == journal.Id).ToListAsync();
            Assert.Equal(before.Count, after.Count);
            foreach (var line in before) Assert.Contains(after, x => x.AccountId == line.AccountId && x.AmountMinor == line.AmountMinor && x.Side != line.Side);
            var feeAccount = await db.Accounts.SingleAsync(x => x.Kind == AccountKind.PlatformFeeRevenue);
            Assert.Equal(0, (await db.Balances.SingleAsync(x => x.AccountId == feeAccount.Id)).BalanceMinor);
            Assert.Equal(1, await sp.GetRequiredService<RiskLimitsDbContext>().Operations.CountAsync()); return true;
        });
        Assert.Equal(TransferStatus.Completed, (await Stored(w.Transfer.Id)).Status);
        var originalHistory = await Read<TransactionReceipt>(await w.Setup.A.Client.GetAsync($"/api/v1/transactions/{w.Transfer.Id}"));
        var reversal = await Read<TransactionReceipt>(await w.Setup.A.Client.GetAsync($"/api/v1/transactions/{w.Request.Id}"));
        Assert.Equal("Reversed", originalHistory.Status); Assert.Equal(w.Request.Id, originalHistory.ReversalTransactionId);
        Assert.Equal("Reversal", reversal.Type); Assert.Equal("Incoming", reversal.Direction); Assert.Equal(1000 + fee, reversal.BalanceChangeMinor);
        Assert.Equal(w.Transfer.Id, reversal.OriginalTransactionId);
        Assert.Equal(2, (await Read<TransactionPage>(await w.Setup.A.Client.GetAsync("/api/v1/transactions"))).Items.Count);
        Assert.Single((await Read<TransactionPage>(await w.Setup.A.Client.GetAsync("/api/v1/transactions?type=Reversal"))).Items);
        Assert.Single((await Read<TransactionPage>(await w.Setup.A.Client.GetAsync("/api/v1/transactions?status=Reversed"))).Items);
        Assert.Equal(receipt, await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof)));
        await Error(await Approve(w.Checker, w.Request.Id, proof with { Note = "Changed" }), "IDEMPOTENCY_KEY_REUSED", HttpStatusCode.Conflict);
        await Error(await Approve(w.Checker, w.Request.Id, proof, "another"), "ALREADY_REVERSED", HttpStatusCode.Conflict);
        await Error(await AdminPost(w.Maker, $"/api/v1/admin/transfers/{w.Transfer.Id}/reversal-requests", new RequestReversal("Again"), "another"), "ALREADY_REVERSED", HttpStatusCode.Conflict);
    }
    [Fact]
    public async Task Reversal_requires_permissions_reason_completed_original_and_separate_approver()
    {
        var s = await SetupAsync(); var pending = await Intent(s);
        Assert.Equal(HttpStatusCode.Forbidden, (await AdminPost(s.A, $"/api/v1/admin/transfers/{pending.Id}/reversal-requests", new RequestReversal("Reason"), "key")).StatusCode);
        await Grant(s.A, "transaction.reverse.request", "transaction.reverse.approve");
        await Error(await AdminPost(s.A, $"/api/v1/admin/transfers/{pending.Id}/reversal-requests", new RequestReversal("Reason"), "key"), "NOT_REVERSIBLE", HttpStatusCode.UnprocessableEntity);
        var t = await Read<TransferReceipt>(await Confirm(s.A, pending.Id));
        await Error(await AdminPost(s.A, $"/api/v1/admin/transfers/{t.Id}/reversal-requests", new RequestReversal(" "), "key"), "VALIDATION_ERROR", HttpStatusCode.BadRequest);
        await Error(await AdminPost(s.A, $"/api/v1/admin/transfers/{t.Id}/reversal-requests", new RequestReversal("Reason")), "IDEMPOTENCY_KEY_REQUIRED", HttpStatusCode.BadRequest);
        var r = await RequestReverse(s.A, t.Id);
        await Error(await s.A.Client.PostAsync($"/api/v1/admin/reversal-requests/{r.Id}/otp", null), "SELF_APPROVAL_FORBIDDEN", HttpStatusCode.Forbidden);
        await Error(await Approve(s.A, r.Id, new(null, Guid.NewGuid(), "123456")), "SELF_APPROVAL_FORBIDDEN", HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, (await Approve(s.B, r.Id, new(null, Guid.NewGuid(), "123456"))).StatusCode);
    }
    [Fact]
    public async Task Concurrent_request_creation_is_idempotent_and_other_keys_cannot_duplicate_pending_request()
    {
        var w = await Workflow();
        var same = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => RequestReverse(w.Maker, w.Transfer.Id)));
        Assert.All(same, r => Assert.Equal(w.Request, r)); Assert.Equal(1, await HistoryDb(db => db.ReversalRequests.CountAsync()));
        await Error(await AdminPost(w.Maker, $"/api/v1/admin/transfers/{w.Transfer.Id}/reversal-requests", new RequestReversal("Changed"), "request"), "IDEMPOTENCY_KEY_REUSED", HttpStatusCode.Conflict);
        await Error(await AdminPost(w.Maker, $"/api/v1/admin/transfers/{w.Transfer.Id}/reversal-requests", new RequestReversal("Same"), "second"), "REVERSAL_PENDING", HttpStatusCode.Conflict);
    }
    [Fact]
    public async Task One_hundred_concurrent_approvals_return_one_compensation_and_identical_receipts()
    {
        var w = await Workflow(25); var proof = await Proof(w.Checker, w.Request.Id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Approve(w.Checker, w.Request.Id, proof)));
        var receipts = new List<ReversalReceipt>(); foreach (var response in responses) receipts.Add(await Read<ReversalReceipt>(response));
        Assert.Single(receipts.Distinct()); Assert.Equal(2, await HistoryDb(db => db.Transactions.CountAsync()));
        Assert.Equal(1, await Scope(sp => sp.GetRequiredService<LedgerDbContext>().Journals.CountAsync(x => x.ReversalOf != null)));
        Assert.Equal(10000, (await HistoryBalance(w.Setup.A))!.LedgerBalance);
    }
    [Fact]
    public async Task Insufficient_recovery_funds_rejects_atomically_then_same_proof_can_recover()
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id);
        await Scope(async sp =>
        {
            var a = sp.GetRequiredService<ILedgerAccounts>(); var recipient = await a.EnsureWalletAccountAsync(w.Setup.B.WalletId, "SAR");
            var control = await a.EnsureSystemAccountAsync(SystemAccount.AdjustmentControl, "SAR");
            return await sp.GetRequiredService<ILedgerPosting>().PostAsync(new("RecoveryFundingTest", "spent", "SAR", [new(recipient, PostingSide.Debit, 1000), new(control, PostingSide.Credit, 1000)], "Recipient spent funds"));
        });
        await Error(await Approve(w.Checker, w.Request.Id, proof), "INSUFFICIENT_RECOVERY_FUNDS", HttpStatusCode.UnprocessableEntity);
        Assert.Equal("Pending", (await Read<ReversalReceipt>(await w.Checker.Client.GetAsync($"/api/v1/admin/reversal-requests/{w.Request.Id}"))).Status);
        Assert.Equal(1, await HistoryDb(db => db.Transactions.CountAsync())); Assert.Equal(9000, (await HistoryBalance(w.Setup.A))!.LedgerBalance);
        await Fund(w.Setup.B, 1000); await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof));
        Assert.Equal(10000, (await HistoryBalance(w.Setup.A))!.LedgerBalance);
    }
    [Theory]
    [InlineData("AfterOtp")]
    [InlineData("AfterLedger")]
    [InlineData("BeforeCommit")]
    public async Task Reversal_fault_rolls_back_money_and_history_and_preserves_otp_recovery(string point)
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id); _factory.ReversalFaults.Fail(point);
        Assert.Equal(HttpStatusCode.InternalServerError, (await Approve(w.Checker, w.Request.Id, proof)).StatusCode);
        Assert.Equal(9000, (await HistoryBalance(w.Setup.A))!.LedgerBalance); Assert.Equal(1000, (await HistoryBalance(w.Setup.B))!.LedgerBalance);
        Assert.Equal(1, await HistoryDb(db => db.Transactions.CountAsync()));
        Assert.Equal(0, await Scope(sp => sp.GetRequiredService<LedgerDbContext>().Journals.CountAsync(x => x.ReversalOf != null)));
        await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof)); Assert.Equal(10000, (await HistoryBalance(w.Setup.A))!.LedgerBalance);
    }
    [Fact]
    public async Task Lost_reversal_commit_response_replays_the_committed_receipt()
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id); _factory.ReversalFaults.Fail("AfterCommit");
        Assert.Equal(HttpStatusCode.InternalServerError, (await Approve(w.Checker, w.Request.Id, proof)).StatusCode);
        var committed = await Read<ReversalReceipt>(await w.Checker.Client.GetAsync($"/api/v1/admin/reversal-requests/{w.Request.Id}"));
        Assert.Equal("Completed", committed.Status); Assert.Equal(committed, await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof)));
        Assert.Equal(2, await HistoryDb(db => db.Transactions.CountAsync()));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Reversal_serialization_failures_retry_the_whole_transaction(int count)
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id); _factory.ReversalFaults.Fail("AfterLedger", count, true);
        var response = await Approve(w.Checker, w.Request.Id, proof);
        if (count == 4)
        {
            await Error(response, "CONCURRENCY_CONFLICT", HttpStatusCode.Conflict);
            Assert.Equal(1, await HistoryDb(db => db.Transactions.CountAsync())); Assert.Equal(9000, (await HistoryBalance(w.Setup.A))!.LedgerBalance);
            response = await Approve(w.Checker, w.Request.Id, proof);
        }
        await Read<ReversalReceipt>(response); Assert.Equal(10000, (await HistoryBalance(w.Setup.A))!.LedgerBalance);
        Assert.Equal(1, await Scope(sp => sp.GetRequiredService<LedgerDbContext>().Journals.CountAsync(x => x.ReversalOf != null)));
    }
    [Fact]
    public async Task Reversal_otp_rejects_wrong_owner_wrong_resource_expiry_and_attempt_exhaustion()
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id); var other = await ActorAsync("OtherChecker"); await Grant(other, "transaction.reverse.approve");
        await Error(await Approve(other, w.Request.Id, proof), "OTP_INVALID", HttpStatusCode.BadRequest);
        var secondTransfer = await Complete(w.Setup); var second = await RequestReverse(w.Maker, secondTransfer.Id, "second");
        await Error(await Approve(w.Checker, second.Id, proof, "second"), "OTP_INVALID", HttpStatusCode.BadRequest);
        for (var i = 0; i < 4; i++) await Error(await Approve(w.Checker, w.Request.Id, proof with { Code = "wrong" }), "OTP_INVALID", HttpStatusCode.BadRequest);
        await Error(await Approve(w.Checker, w.Request.Id, proof with { Code = "wrong" }), "OTP_ATTEMPTS_EXCEEDED", HttpStatusCode.BadRequest);
        _factory.Time.Advance(TimeSpan.FromSeconds(61)); proof = await Proof(w.Checker, w.Request.Id);
        _factory.Time.Advance(TimeSpan.FromMinutes(11));
        await Error(await Approve(w.Checker, w.Request.Id, proof), "OTP_EXPIRED", HttpStatusCode.BadRequest);
        Assert.Equal(2, await HistoryDb(db => db.Transactions.CountAsync()));
    }
    [Fact]
    public async Task Rejected_request_is_retained_without_money_and_allows_new_request()
    {
        var w = await Workflow();
        var rejected = await Read<ReversalReceipt>(await w.Checker.Client.PostAsJsonAsync($"/api/v1/admin/reversal-requests/{w.Request.Id}/reject", new RejectReversal("Unsupported claim")));
        Assert.Equal("Rejected", rejected.Status); Assert.Equal(w.Checker.IdentityId, rejected.DecidedBy); Assert.Equal(1, await HistoryDb(db => db.Transactions.CountAsync()));
        await Error(await Approve(w.Checker, w.Request.Id, new(null, Guid.NewGuid(), "123456")), "STATE_CONFLICT", HttpStatusCode.Conflict);
        var replacement = await RequestReverse(w.Maker, w.Transfer.Id, "replacement"); Assert.NotEqual(w.Request.Id, replacement.Id);
        Assert.Equal(2, await HistoryDb(db => db.ReversalRequests.CountAsync()));
    }
    [Fact]
    public async Task Transaction_projection_failure_rolls_back_the_transfer_and_limit_usage()
    {
        var s = await SetupAsync(); var t = await Intent(s); _factory.TransferFaults.Fail("AfterHistory");
        Assert.Equal(HttpStatusCode.InternalServerError, (await Confirm(s.A, t.Id)).StatusCode);
        Assert.Equal(0, await HistoryDb(db => db.Transactions.CountAsync())); await NoFinancialEffect(t.Id, 10000, s.A);
        await Read<TransferReceipt>(await Confirm(s.A, t.Id)); Assert.Equal(1, await HistoryDb(db => db.Transactions.CountAsync()));
    }
    [Fact]
    public async Task PostgreSQL_retains_original_facts_and_reversal_decisions_and_rejects_partial_decisions()
    {
        var w = await Workflow();
        await using var connection = new NpgsqlConnection(_connection); await connection.OpenAsync();
        foreach (var sql in new[]
        {
            $"UPDATE transactions.transactions SET \"AmountMinor\" = 999 WHERE \"Id\" = '{w.Transfer.Id}'",
            $"DELETE FROM transactions.transactions WHERE \"Id\" = '{w.Transfer.Id}'",
            "TRUNCATE transactions.reversal_requests, transactions.transactions",
            $"UPDATE transactions.reversal_requests SET \"Reason\" = 'Changed' WHERE \"Id\" = '{w.Request.Id}'",
            $"DELETE FROM transactions.reversal_requests WHERE \"Id\" = '{w.Request.Id}'",
            $"UPDATE transactions.reversal_requests SET \"Status\" = 'Completed', \"DecidedBy\" = '{w.Checker.IdentityId}', \"DecidedAt\" = now() WHERE \"Id\" = '{w.Request.Id}'"
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync()); Assert.Equal("23514", ex.SqlState);
        }
        var proof = await Proof(w.Checker, w.Request.Id); await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof));
        await using var mutation = new NpgsqlCommand($"UPDATE transactions.reversal_requests SET \"DecisionNote\" = 'Changed' WHERE \"Id\" = '{w.Request.Id}'", connection);
        await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Cursor_preserves_from_only_range_even_when_next_request_would_exceed_the_maximum()
    {
        var s = await SetupAsync();
        await Complete(s); await Complete(s); await Complete(s);
        var now = _factory.Time.GetUtcNow();
        var from = now.AddTicks(10 - now.Ticks % 10).AddDays(-366);
        var url = "/api/v1/transactions?pageSize=1&from=" + Uri.EscapeDataString(from.ToString("O"));
        var first = await Read<TransactionPage>(await s.A.Client.GetAsync(url)); Assert.NotNull(first.NextCursor);
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        var next = await Read<TransactionPage>(await s.A.Client.GetAsync(url + "&cursor=" + Uri.EscapeDataString(first.NextCursor!)));
        Assert.Single(next.Items); Assert.NotEqual(first.Items[0].Id, next.Items[0].Id);
        await Error(await s.A.Client.GetAsync(url), "DATE_RANGE_TOO_LARGE", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Recovery_and_a_new_transfer_cannot_spend_the_same_recipient_funds()
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id);
        var reverseDirection = new Setup(w.Setup.B, w.Setup.A, await Beneficiary(w.Setup.B, w.Setup.A));
        var outgoing = await Intent(reverseDirection);
        var responses = await Task.WhenAll(Approve(w.Checker, w.Request.Id, proof), Confirm(w.Setup.B, outgoing.Id));
        Assert.Equal(1, responses.Count(x => x.IsSuccessStatusCode));
        var rejected = responses.Single(x => !x.IsSuccessStatusCode);
        await Error(rejected, responses[0].IsSuccessStatusCode ? "INSUFFICIENT_BALANCE" : "INSUFFICIENT_RECOVERY_FUNDS", HttpStatusCode.UnprocessableEntity);
        Assert.Equal(10000, await Balance(w.Setup.A)); Assert.Equal(0, await Balance(w.Setup.B));
        Assert.Equal(2, await HistoryDb(db => db.Transactions.CountAsync()));
    }

    [Fact]
    public async Task Concurrent_approve_and_reject_commit_exactly_one_terminal_decision()
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id);
        var responses = await Task.WhenAll(Approve(w.Checker, w.Request.Id, proof),
            w.Checker.Client.PostAsJsonAsync($"/api/v1/admin/reversal-requests/{w.Request.Id}/reject", new RejectReversal("Not justified")));
        Assert.Equal(1, responses.Count(x => x.IsSuccessStatusCode));
        await Error(responses.Single(x => !x.IsSuccessStatusCode), "STATE_CONFLICT", HttpStatusCode.Conflict);
        var receipt = await Read<ReversalReceipt>(responses.Single(x => x.IsSuccessStatusCode));
        Assert.Equal(receipt.Status == "Completed" ? 10000 : 9000, await Balance(w.Setup.A));
        Assert.Equal(receipt.Status == "Completed" ? 2 : 1, await HistoryDb(db => db.Transactions.CountAsync()));
    }

    [Fact]
    public async Task Successful_approval_key_cannot_be_reused_for_another_request()
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id);
        await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof));
        var second = await Complete(w.Setup); var r = await RequestReverse(w.Maker, second.Id, "second");
        var secondProof = await Proof(w.Checker, r.Id);
        await Error(await Approve(w.Checker, r.Id, secondProof), "IDEMPOTENCY_KEY_REUSED", HttpStatusCode.Conflict);
        Assert.Equal(3, await HistoryDb(db => db.Transactions.CountAsync()));
        await Read<ReversalReceipt>(await Approve(w.Checker, r.Id, secondProof, "second"));
        Assert.Equal(10000, await Balance(w.Setup.A));
    }

    [Fact]
    public async Task Runtime_grants_allow_locks_and_keep_history_immutable()
    {
        var w = await Workflow();
        var role = "phase8_runtime_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_connection); await connection.OpenAsync();
        try
        {
            await using (var grant = new NpgsqlCommand($"""
                CREATE ROLE "{role}" NOLOGIN;
                GRANT USAGE ON SCHEMA transactions TO "{role}";
                GRANT SELECT, INSERT ON transactions.transactions, transactions.reversal_requests TO "{role}";
                GRANT UPDATE ("Id") ON transactions.transactions TO "{role}";
                GRANT UPDATE ("Status", "DecidedBy", "DecidedAt", "DecisionNote", "ApprovalKey", "ApprovalHash", "OtpChallengeId", "ReversalTransactionId") ON transactions.reversal_requests TO "{role}";
                SET ROLE "{role}";
                """, connection)) await grant.ExecuteNonQueryAsync();
            await using (var locked = new NpgsqlCommand($"SELECT \"Id\" FROM transactions.transactions WHERE \"Id\" = '{w.Transfer.Id}' FOR UPDATE", connection))
                Assert.Equal(w.Transfer.Id, await locked.ExecuteScalarAsync());
            await using (var locked = new NpgsqlCommand($"SELECT \"Id\" FROM transactions.reversal_requests WHERE \"Id\" = '{w.Request.Id}' FOR UPDATE", connection))
                Assert.Equal(w.Request.Id, await locked.ExecuteScalarAsync());
            await using (var mutate = new NpgsqlCommand($"UPDATE transactions.transactions SET \"Id\" = \"Id\" WHERE \"Id\" = '{w.Transfer.Id}'", connection))
                Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync())).SqlState);
            await using (var delete = new NpgsqlCommand($"DELETE FROM transactions.transactions WHERE \"Id\" = '{w.Transfer.Id}'", connection))
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"RESET ROLE; DROP OWNED BY \"{role}\"; DROP ROLE IF EXISTS \"{role}\";", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
