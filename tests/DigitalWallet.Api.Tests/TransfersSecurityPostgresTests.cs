using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalWallet.Beneficiaries.Infrastructure;
using DigitalWallet.Customers.Domain;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Ledger.Infrastructure;
using DigitalWallet.RiskLimits.Infrastructure;
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

namespace DigitalWallet.Api.Tests;
public sealed partial class TransfersPostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_OTP_issuance_responses_never_leave_a_superseded_challenge(bool delayOlder)
    {
        var s = await SetupAsync(); var t = await Intent(s);
        _factory.Time.Advance(TimeSpan.FromSeconds(61));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        _factory.TransferFaults.Observe = async (point, _) =>
        {
            if (point != "AfterOtpIssue") return;
            var n = Interlocked.Increment(ref count);
            if (n == (delayOlder ? 1 : 2)) { reached.TrySetResult(); await release.Task; }
        };
        var older = s.A.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null);
        if (delayOlder) await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); else await Read<TransferReceipt>(await older);
        _factory.Time.Advance(TimeSpan.FromSeconds(61));
        var newer = s.A.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null);
        if (delayOlder) await Read<TransferReceipt>(await newer); else await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        if (delayOlder) await Read<TransferReceipt>(await older); else await Read<TransferReceipt>(await newer);
        var current = await Stored(t.Id);
        Assert.Null(await Scope(sp => sp.GetRequiredService<IdentityDbContext>().OtpChallenges.Where(x => x.Id == current.OtpChallengeId).Select(x => x.SupersededAt).SingleAsync()));
        Assert.Equal("Completed", (await Read<TransferReceipt>(await Confirm(s.A, t.Id))).Status);
    }
    [Fact]
    public async Task Client_supplied_customer_target_wallet_and_fee_are_ignored()
    {
        var s = await SetupAsync(fee: 10);
        var response = await s.A.Client.PostAsJsonAsync("/api/v1/transfers/quotes", new
        {
            beneficiaryId = s.BeneficiaryId, amountMinor = 1000, currency = "SAR", feeMinor = 0,
            ownerCustomerId = s.B.CustomerId, senderWalletId = s.B.WalletId, targetWalletId = s.A.WalletId
        });
        var q = await Read<QuoteResponse>(response); Assert.Equal(10, q.FeeMinor);
        var stored = await Db(db => db.Quotes.SingleAsync(x => x.Id == q.Id));
        Assert.Equal(s.A.CustomerId, stored.OwnerCustomerId); Assert.Equal(s.A.WalletId, stored.SenderWalletId); Assert.Equal(s.B.WalletId, stored.TargetWalletId);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("transfer")]
    [InlineData("sender")]
    [InlineData("target")]
    [InlineData("amount")]
    [InlineData("fee")]
    [InlineData("currency")]
    [InlineData("purpose")]
    public async Task OTP_binds_every_financial_dimension_and_rejects_replay(string dimension)
    {
        var s = await SetupAsync(); var t = await Intent(s); var row = await Stored(t.Id);
        var request = new VerifyOtpChallenge(row.OtpChallengeId!.Value, s.A.IdentityId, row.Id, s.B.WalletId,
            OtpChallengePurpose.TransferConfirmation, await Code(row.Id), new(s.A.WalletId, row.AmountMinor, row.FeeMinor, row.Currency));
        var wrong = dimension switch
        {
            "owner" => request with { OwnerIdentityUserId = s.B.IdentityId },
            "transfer" => request with { ResourceId = Guid.NewGuid() },
            "sender" => request with { Transfer = request.Transfer! with { SenderWalletId = Guid.NewGuid() } },
            "target" => request with { TargetWalletId = Guid.NewGuid() },
            "amount" => request with { Transfer = request.Transfer! with { AmountMinor = row.AmountMinor + 1 } },
            "fee" => request with { Transfer = request.Transfer! with { FeeMinor = row.FeeMinor + 1 } },
            "currency" => request with { Transfer = request.Transfer! with { Currency = "USD" } },
            _ => request with { Purpose = OtpChallengePurpose.BeneficiaryActivation, Transfer = null }
        };
        Assert.Equal(OtpVerificationStatus.Invalid, await Scope(sp => sp.GetRequiredService<IOtpChallengeService>().VerifyAsync(wrong, default)));
        Assert.Equal(OtpVerificationStatus.Verified, await Scope(sp => sp.GetRequiredService<IOtpChallengeService>().VerifyAsync(request, default)));
        Assert.Equal(OtpVerificationStatus.Invalid, await Scope(sp => sp.GetRequiredService<IOtpChallengeService>().VerifyAsync(request, default)));
        Assert.Equal(OtpVerificationReceiptStatus.NotVerified, await Scope(sp => sp.GetRequiredService<IOtpChallengeService>().GetVerificationReceiptAsync(wrong, default)));
        Assert.Equal(OtpVerificationReceiptStatus.Verified, await Scope(sp => sp.GetRequiredService<IOtpChallengeService>().GetVerificationReceiptAsync(request, default)));
    }
    [Fact]
    public async Task Expired_OTP_and_exhausted_attempts_cannot_post()
    {
        var s = await SetupAsync(); var t = await Intent(s);
        await _factory.ExpireAsync((await Stored(t.Id)).OtpChallengeId!.Value);
        await Error(await Confirm(s.A, t.Id), "OTP_EXPIRED");
        var other = await Intent(s); var actual = await Code(other.Id); var wrong = actual == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++) await Error(await Confirm(s.A, other.Id, wrong), i == 4 ? "OTP_ATTEMPTS_EXCEEDED" : "OTP_INVALID");
        await Error(await Confirm(s.A, other.Id), "OTP_ATTEMPTS_EXCEEDED");
        await NoFinancialEffect(t.Id, 10000, s.A);
    }
    [Fact]
    public async Task Transfer_expiry_and_quote_expiry_while_waiting_prevent_posting()
    {
        var s = await SetupAsync(); var t = await Intent(s);
        _factory.TransferFaults.Observe = (point, _) => { if (point == "AfterLedger") _factory.Time.Advance(TimeSpan.FromSeconds(300)); return Task.CompletedTask; };
        await Error(await Confirm(s.A, t.Id), "QUOTE_EXPIRED");
        await NoFinancialEffect(t.Id, 10000, s.A);
        await Error(await Confirm(s.A, t.Id), "TRANSFER_EXPIRED");
        await Error(await s.A.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null), "TRANSFER_EXPIRED");
    }
    [Fact]
    public async Task Resend_has_cooldown_and_attempt_budget_and_supersedes_old_binding()
    {
        var s = await SetupAsync(); var t = await Intent(s); var first = await Stored(t.Id); var code = await Code(t.Id);
        await Read<TransferReceipt>(await s.A.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null));
        Assert.Equal(first.OtpChallengeId, (await Stored(t.Id)).OtpChallengeId);
        _factory.Time.Advance(TimeSpan.FromSeconds(61));
        await Read<TransferReceipt>(await s.A.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null));
        Assert.NotEqual(first.OtpChallengeId, (await Stored(t.Id)).OtpChallengeId);
        var old = new VerifyOtpChallenge(first.OtpChallengeId!.Value, s.A.IdentityId, t.Id, s.B.WalletId, OtpChallengePurpose.TransferConfirmation, code, new(s.A.WalletId, 1000, 0, "SAR"));
        Assert.Equal(OtpVerificationStatus.Invalid, await Scope(sp => sp.GetRequiredService<IOtpChallengeService>().VerifyAsync(old, default)));
        for (var i = 0; i < 3; i++)
        {
            _factory.Time.Advance(TimeSpan.FromSeconds(61));
            await Read<TransferReceipt>(await s.A.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null));
        }
        Assert.InRange(await Scope(sp => sp.GetRequiredService<IdentityDbContext>().OtpChallenges.CountAsync(x => x.Purpose == OtpPurpose.TransferConfirmation)), 1, 3);
        Assert.Equal("Completed", (await Read<TransferReceipt>(await Confirm(s.A, t.Id))).Status);
    }
    [Fact]
    public async Task Resend_HTTP_rate_limit_is_per_authenticated_owner()
    {
        var s = await SetupAsync(); var t = await Intent(s);
        for (var i = 0; i < 8; i++) await Read<TransferReceipt>(await s.A.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await s.A.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.B.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null)).StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OTP_issuance_failure_or_response_loss_is_recoverable_without_duplicate_intent(bool lostResponse)
    {
        var s = await SetupAsync(); var q = await Quote(s);
        if (lostResponse) _factory.FailAfterOtpCreateResponses = 1; else _factory.FailOtpCreateRequests = 1;
        Assert.Equal(HttpStatusCode.InternalServerError, (await CreateResponse(s.A, q.Id, "issue-failure")).StatusCode);
        var t = await Read<TransferReceipt>(await CreateResponse(s.A, q.Id, "issue-failure"), HttpStatusCode.Created);
        Assert.NotNull((await Stored(t.Id)).OtpChallengeId);
        Assert.Equal(1, await Scope(sp => sp.GetRequiredService<IdentityDbContext>().OtpChallenges.CountAsync(x => x.Purpose == OtpPurpose.TransferConfirmation)));
        Assert.Equal(1, await Db(db => db.Transfers.CountAsync())); Assert.Equal("Completed", (await Read<TransferReceipt>(await Confirm(s.A, t.Id))).Status);
    }
    [Fact]
    public async Task Resend_after_OTP_commit_preserves_durable_recovery_proof()
    {
        var s = await SetupAsync(); var t = await Intent(s); var before = (await Stored(t.Id)).OtpChallengeId;
        _factory.TransferFaults.Fail("AfterOtp"); Assert.Equal(HttpStatusCode.InternalServerError, (await Confirm(s.A, t.Id)).StatusCode);
        _factory.Time.Advance(TimeSpan.FromSeconds(61));
        await Read<TransferReceipt>(await s.A.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null));
        Assert.Equal(before, (await Stored(t.Id)).OtpChallengeId);
        Assert.Equal("Completed", (await Read<TransferReceipt>(await Confirm(s.A, t.Id))).Status);
    }
    [Fact]
    public async Task BOLA_matrix_hides_quotes_and_transfers_and_separates_sender_from_recipient()
    {
        var s = await SetupAsync(); var outsider = await ActorAsync("Outsider"); var q = await Quote(s);
        foreach (var other in new[] { s.B, outsider })
        {
            await Error(await CreateResponse(other, q.Id, "stolen"), "QUOTE_NOT_FOUND", HttpStatusCode.NotFound);
            await Error(await other.Client.PostAsJsonAsync("/api/v1/transfers/quotes", new QuoteRequest(s.BeneficiaryId, 1, "SAR")), "BENEFICIARY_NOT_FOUND");
        }
        var t = await Read<TransferReceipt>(await CreateResponse(s.A, q.Id, "owner"), HttpStatusCode.Created);
        foreach (var other in new[] { s.B, outsider })
        {
            await Error(await Confirm(other, t.Id, "123456"), "TRANSFER_NOT_FOUND", HttpStatusCode.NotFound);
            await Error(await other.Client.PostAsync($"/api/v1/transfers/{t.Id}/otp", null), "TRANSFER_NOT_FOUND", HttpStatusCode.NotFound);
        }
        await Error(await outsider.Client.GetAsync($"/api/v1/transfers/{t.Id}"), "TRANSFER_NOT_FOUND", HttpStatusCode.NotFound);
        Assert.Empty((await Read<TransferPage>(await outsider.Client.GetAsync("/api/v1/transfers"))).Items);
        Assert.Equal(t.Id, (await Read<TransferReceipt>(await s.B.Client.GetAsync($"/api/v1/transfers/{t.Id}"))).Id);
        Assert.Single((await Read<TransferPage>(await s.A.Client.GetAsync("/api/v1/transfers"))).Items);
        Assert.Single((await Read<TransferPage>(await s.B.Client.GetAsync("/api/v1/transfers"))).Items);
        await NoFinancialEffect(t.Id, 10000, s.A);
    }
    [Fact]
    public async Task Staff_requires_explicit_view_any_permission()
    {
        var s = await SetupAsync(); var t = await Intent(s); var staff = await ActorAsync("Staff");
        await Error(await staff.Client.GetAsync($"/api/v1/transfers/{t.Id}"), "TRANSFER_NOT_FOUND");
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<IdentityDbContext>(); var role = new ApplicationRole { Id = Guid.NewGuid(), Name = "TransferReader", NormalizedName = "TRANSFERREADER" };
            db.Roles.Add(role); db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = "transfer.view.any" });
            db.UserRoles.Add(new ApplicationUserRole { RoleId = role.Id, UserId = staff.IdentityId }); await db.SaveChangesAsync(); return true;
        });
        Assert.Equal(t.Id, (await Read<TransferReceipt>(await staff.Client.GetAsync($"/api/v1/transfers/{t.Id}"))).Id);
        Assert.Single((await Read<TransferPage>(await staff.Client.GetAsync("/api/v1/transfers"))).Items);
        await Error(await Confirm(staff, t.Id, "123456"), "TRANSFER_NOT_FOUND");
    }
    [Fact]
    public async Task Keyset_pages_are_stable_for_equal_timestamps_and_newer_inserts()
    {
        var s = await SetupAsync(); var all = new HashSet<Guid>();
        for (var i = 0; i < 12; i++) all.Add((await Intent(s)).Id);
        var page = await Read<TransferPage>(await s.A.Client.GetAsync("/api/v1/transfers?pageSize=3"));
        var seen = page.Items.Select(x => x.Id).ToList(); Assert.NotNull(page.NextCursor);
        _factory.Time.Advance(TimeSpan.FromSeconds(1)); var newer = await Intent(s);
        while (page.NextCursor != null)
        {
            page = await Read<TransferPage>(await s.A.Client.GetAsync("/api/v1/transfers?pageSize=3&cursor=" + Uri.EscapeDataString(page.NextCursor)));
            seen.AddRange(page.Items.Select(x => x.Id));
        }
        Assert.Equal(12, seen.Count); Assert.Equal(12, seen.Distinct().Count()); Assert.True(all.SetEquals(seen)); Assert.DoesNotContain(newer.Id, seen);
        await Error(await s.A.Client.GetAsync("/api/v1/transfers?cursor=invalid"), "VALIDATION_ERROR");
    }
    [Theory]
    [InlineData("customer", "CUSTOMER_NOT_ACTIVE")]
    [InlineData("kyc", "CUSTOMER_NOT_ACTIVE")]
    [InlineData("source", "WALLET_NOT_ACTIVE")]
    [InlineData("target", "WALLET_NOT_ACTIVE")]
    [InlineData("beneficiary", "BENEFICIARY_NOT_ACTIVE")]
    public async Task Authoritative_states_are_revalidated_at_confirmation(string change, string code)
    {
        var s = await SetupAsync(); var t = await Intent(s);
        await Scope(async sp =>
        {
            if (change is "customer" or "kyc")
            {
                var db = sp.GetRequiredService<CustomersDbContext>(); var c = await db.Customers.SingleAsync(x => x.Id == s.A.CustomerId);
                if (change == "customer") c.Status = CustomerStatus.Suspended; else c.KycStatus = KycStatus.Expired;
                c.ConcurrencyVersion = Guid.NewGuid(); await db.SaveChangesAsync();
            }
            else if (change == "beneficiary")
            {
                var db = sp.GetRequiredService<BeneficiariesDbContext>(); (await db.Beneficiaries.SingleAsync(x => x.Id == s.BeneficiaryId)).Delete(_factory.Time.GetUtcNow()); await db.SaveChangesAsync();
            }
            else
            {
                var db = sp.GetRequiredService<WalletsDbContext>(); var id = change == "source" ? s.A.WalletId : s.B.WalletId;
                var w = await db.Wallets.SingleAsync(x => x.Id == id); w.Status = WalletStatus.Frozen; w.ConcurrencyVersion = Guid.NewGuid(); await db.SaveChangesAsync();
            }
            return true;
        });
        await Error(await Confirm(s.A, t.Id), code); await NoFinancialEffect(t.Id, 10000, s.A);
    }
    [Fact]
    public async Task Self_transfer_is_rejected_even_with_an_internal_self_beneficiary()
    {
        var s = await SetupAsync(); var self = await Beneficiary(s.A, s.A);
        await Error(await s.A.Client.PostAsJsonAsync("/api/v1/transfers/quotes", new QuoteRequest(self, 100, "SAR")), "SELF_TRANSFER_NOT_ALLOWED");
    }
    [Fact]
    public async Task Freeze_waits_for_transfer_commit_and_no_partial_state_is_visible()
    {
        var s = await SetupAsync(); var t = await Intent(s);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.TransferFaults.Observe = async (point, _) => { if (point == "AfterLedger") { reached.TrySetResult(); await release.Task; } };
        var confirmation = Confirm(s.A, t.Id);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task<WalletResponse>? freezing = null;
        try
        {
            await NoFinancialEffect(t.Id, 10000, s.A); // Other connections still see the pre-commit state.
            freezing = Scope(async sp =>
            {
                var w = await sp.GetRequiredService<WalletsDbContext>().Wallets.AsNoTracking().SingleAsync(x => x.Id == s.A.WalletId);
                return await sp.GetRequiredService<IWalletService>().FreezeAsync(w.Id, new("Race freeze", w.ConcurrencyVersion), Guid.NewGuid(), default);
            });
            Assert.NotSame(freezing, await Task.WhenAny(freezing, Task.Delay(150)));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("Completed", (await Read<TransferReceipt>(await confirmation)).Status);
        Assert.Equal(WalletStatus.Frozen, (await freezing!).Status); Assert.Equal(9000, await Balance(s.A)); Assert.Equal(1, await Journals());
    }
    [Fact]
    public async Task Freeze_winning_the_wallet_lock_prevents_the_waiting_transfer()
    {
        var s = await SetupAsync(); var t = await Intent(s);
        await using var c = new NpgsqlConnection(_connection); await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("UPDATE wallets.wallets SET \"Status\" = 'Frozen', \"ConcurrencyVersion\" = @version WHERE \"Id\" = @id", c, tx);
        cmd.Parameters.AddWithValue("id", s.A.WalletId); cmd.Parameters.AddWithValue("version", Guid.NewGuid()); await cmd.ExecuteNonQueryAsync();
        var confirmation = Confirm(s.A, t.Id);
        Assert.NotSame(confirmation, await Task.WhenAny(confirmation, Task.Delay(150)));
        await tx.CommitAsync();
        await Error(await confirmation, "WALLET_NOT_ACTIVE"); await NoFinancialEffect(t.Id, 10000, s.A);
    }
    [Theory]
    [InlineData("zero")]
    [InlineData("negative-fee")]
    [InlineData("wrong-total")]
    [InlineData("self")]
    public async Task PostgreSQL_checks_reject_invalid_quote_financial_data(string kind)
    {
        var s = await SetupAsync(); var q = await Quote(s);
        await using var scope = _factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TransfersDbContext>();
        var clone = await db.Quotes.AsNoTracking().SingleAsync(x => x.Id == q.Id); clone.Id = Guid.NewGuid();
        if (kind == "zero") { clone.AmountMinor = 0; clone.TotalDebitMinor = 0; }
        if (kind == "negative-fee") { clone.FeeMinor = -1; clone.TotalDebitMinor--; }
        if (kind == "wrong-total") clone.TotalDebitMinor++;
        if (kind == "self") clone.TargetWalletId = clone.SenderWalletId;
        db.Quotes.Add(clone);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
    [Fact]
    public async Task Retry_exhaustion_leaves_no_financial_state_and_returns_concurrency_conflict()
    {
        var s = await SetupAsync(); var t = await Intent(s);
        _factory.TransferFaults.Fail("AfterLedger", 4, serialization: true);
        await Error(await Confirm(s.A, t.Id), "CONCURRENCY_CONFLICT", HttpStatusCode.Conflict);
        await NoFinancialEffect(t.Id, 10000, s.A);
        Assert.Equal("Completed", (await Read<TransferReceipt>(await Confirm(s.A, t.Id))).Status);
    }

    [Fact]
    public async Task One_quote_concurrently_claimed_by_different_keys_has_one_intent()
    {
        var s = await SetupAsync(); var q = await Quote(s);
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => CreateResponse(s.A, q.Id, "quote-race-" + i)));
        Assert.Single(results, r => r.IsSuccessStatusCode);
        foreach (var r in results.Where(r => !r.IsSuccessStatusCode)) await Error(r, "QUOTE_USED");
        var t = await Read<TransferReceipt>(results.Single(r => r.IsSuccessStatusCode), HttpStatusCode.Created);
        await Read<TransferReceipt>(await Confirm(s.A, t.Id)); Assert.Equal(1, await Journals());
    }
    [Theory]
    [InlineData("UPDATE transfers.transfers SET \"Status\" = 'PendingConfirmation' WHERE \"Id\" = @id")]
    [InlineData("UPDATE transfers.transfers SET \"AmountMinor\" = 999 WHERE \"Id\" = @id")]
    [InlineData("DELETE FROM transfers.transfers WHERE \"Id\" = @id")]
    [InlineData("TRUNCATE transfers.transfers CASCADE")]
    [InlineData("UPDATE transfers.quotes SET \"UsedAt\" = NULL WHERE \"Id\" = @quote")]
    [InlineData("UPDATE transfers.idempotency SET \"Key\" = 'changed' WHERE \"TransferId\" = @id")]
    public async Task PostgreSQL_guards_terminal_state_financial_immutability_quote_and_idempotency(string sql)
    {
        var s = await SetupAsync(); var t = await Intent(s); await Read<TransferReceipt>(await Confirm(s.A, t.Id)); var row = await Stored(t.Id);
        await using var c = new NpgsqlConnection(_connection); await c.OpenAsync(); await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", t.Id); cmd.Parameters.AddWithValue("quote", row.QuoteId);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
    }
    [Fact]
    public async Task Ledger_participant_uses_external_transaction_and_savepoint_without_committing_or_closing_it()
    {
        var s = await SetupAsync();
        var accounts = await Scope(async sp =>
        {
            var a = sp.GetRequiredService<ILedgerAccounts>();
            return (Sender: await a.EnsureWalletAccountAsync(s.A.WalletId, "SAR"), Target: await a.EnsureWalletAccountAsync(s.B.WalletId, "SAR"));
        });
        await using var c = new NpgsqlConnection(_connection); await c.OpenAsync();
        await using (var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await using (var participant = await LedgerTransactionParticipant.EnlistAsync(c, tx))
            {
                // A SQL failure after one inserted entry must roll back the journal and earlier entries.
                await using var bad = new NpgsqlCommand("CREATE FUNCTION ledger.phase7_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF (SELECT count(*) FROM ledger.entries WHERE \"JournalId\" = NEW.\"JournalId\") >= 1 THEN RAISE EXCEPTION 'injected'; END IF; RETURN NEW; END $$; CREATE TRIGGER phase7_fail BEFORE INSERT ON ledger.entries FOR EACH ROW EXECUTE FUNCTION ledger.phase7_fail();", c, tx);
                await bad.ExecuteNonQueryAsync();
                await Assert.ThrowsAsync<PostgresException>(() => participant.Posting.PostAsync(new("InternalTransfer", "participant-failed", "SAR",
                    [new(accounts.Sender, PostingSide.Debit, 100), new(accounts.Target, PostingSide.Credit, 100)], "Participant test")));
                await using (var remaining = new NpgsqlCommand("SELECT count(*) FROM ledger.journals WHERE \"SourceId\" = 'participant-failed'", c, tx))
                    Assert.Equal(0L, (long)(await remaining.ExecuteScalarAsync())!);
                await using var cleanup = new NpgsqlCommand("DROP TRIGGER phase7_fail ON ledger.entries; DROP FUNCTION ledger.phase7_fail();", c, tx); await cleanup.ExecuteNonQueryAsync();
                await participant.Posting.PostAsync(new("InternalTransfer", "participant-success", "SAR", [new(accounts.Sender, PostingSide.Debit, 100), new(accounts.Target, PostingSide.Credit, 100)], "Participant test"));
                Assert.Equal(0, await Journals()); Assert.Equal(10000, await Balance(s.A));
            }
            Assert.Equal(ConnectionState.Open, c.State); await tx.RollbackAsync();
        }
        Assert.Equal(0, await Journals()); Assert.Equal(10000, await Balance(s.A));
        await using (var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await using var participant = await LedgerTransactionParticipant.EnlistAsync(c, tx);
            await participant.Posting.PostAsync(new("InternalTransfer", "participant-commit", "SAR", [new(accounts.Sender, PostingSide.Debit, 100), new(accounts.Target, PostingSide.Credit, 100)], "Participant test"));
            await tx.CommitAsync();
        }
        Assert.Equal(1, await Journals()); Assert.Equal(9900, await Balance(s.A));
    }
}
