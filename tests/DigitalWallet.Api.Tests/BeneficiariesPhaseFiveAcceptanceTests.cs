using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalWallet.Beneficiaries.Application;
using DigitalWallet.Beneficiaries.Domain;
using DigitalWallet.Beneficiaries.Infrastructure;
using DigitalWallet.Customers.Domain;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Customers.Services;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using DigitalWallet.Wallets.Domain;
using DigitalWallet.Wallets.Persistence;
using DigitalWallet.Wallets.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace DigitalWallet.Api.Tests;

/// <summary>Phase 5 acceptance coverage against a real, disposable PostgreSQL database.</summary>
public sealed class BeneficiariesPhaseFiveAcceptanceTests : IClassFixture<IdentityDatabaseFixture>, IAsyncLifetime
{
    private readonly IdentityPostgresFactory _factory;

    public BeneficiariesPhaseFiveAcceptanceTests(IdentityDatabaseFixture fixture) => _factory = new IdentityPostgresFactory(fixture.ConnectionString);
    public Task InitializeAsync() => _factory.ResetAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Verify_expired_beneficiary_otp_returns_otp_expired()
    {
        var setup = await CreateBeneficiaryAsync("expired");
        await _factory.ExpireAsync(setup.ChallengeId);

        var response = await setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/verify",
            new VerifyBeneficiaryRequest(_factory.Otp.Get(setup.ChallengeId), setup.Beneficiary.ConcurrencyVersion));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("OTP_EXPIRED", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Verify_after_maximum_beneficiary_otp_attempts_returns_otp_attempts_exceeded()
    {
        var setup = await CreateBeneficiaryAsync("attempts");
        var wrongCode = _factory.Otp.Get(setup.ChallengeId) == "000000" ? "111111" : "000000";
        HttpResponseMessage? last = null;
        for (var i = 0; i < 5; i++)
            last = await setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/verify", new VerifyBeneficiaryRequest(wrongCode));

        Assert.NotNull(last);
        Assert.Equal(HttpStatusCode.BadRequest, last.StatusCode);
        Assert.Equal("OTP_ATTEMPTS_EXCEEDED", await ProblemCodeAsync(last));
        var correct = await setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/verify", new VerifyBeneficiaryRequest(_factory.Otp.Get(setup.ChallengeId)));
        Assert.Equal("OTP_ATTEMPTS_EXCEEDED", await ProblemCodeAsync(correct));
    }

    [Fact]
    public async Task Resend_supersedes_the_previous_beneficiary_otp_and_the_old_code_is_invalid()
    {
        var setup = await CreateBeneficiaryAsync("superseded");
        var oldChallenge = setup.ChallengeId;
        var oldCode = _factory.Otp.Get(oldChallenge);
        _factory.Time.Advance(TimeSpan.FromSeconds(61));

        var resend = await setup.Owner.PostAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/otp", null);
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        var newChallenge = await ChallengeIdAsync(setup.Beneficiary.Id);
        Assert.NotEqual(oldChallenge, newChallenge);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            Assert.NotNull((await identity.OtpChallenges.SingleAsync(x => x.Id == oldChallenge)).SupersededAt);
        }

        var verify = await setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/verify", new VerifyBeneficiaryRequest(oldCode));
        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
        Assert.Equal("OTP_INVALID", await ProblemCodeAsync(verify));
    }

    [Fact]
    public async Task Add_retry_after_lost_otp_response_recovers_without_an_extra_beneficiary_or_challenge()
    {
        using var owner = _factory.CreateClient();
        using var recipient = _factory.CreateClient();
        var ownerAuth = await AuthenticateAndActivateAsync(owner, "lost-owner@example.test", "lost-owner", "Owner");
        await AuthenticateAndActivateAsync(recipient, "lost-recipient@example.test", "lost-recipient", "Recipient");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerAuth.AccessToken);
        var wallet = await CreateWalletAsync(recipient);
        _factory.FailAfterOtpCreateResponses = 1;

        var interrupted = await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(wallet.PublicHandle, "Lost response"));
        Assert.Equal(HttpStatusCode.InternalServerError, interrupted.StatusCode);
        Guid issuedChallenge;
        Guid pendingBeneficiary;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var beneficiaries = scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>();
            var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var pending = await beneficiaries.Beneficiaries.SingleAsync();
            pendingBeneficiary = pending.Id;
            Assert.Null(pending.OtpChallengeId);
            issuedChallenge = (await identity.OtpChallenges.SingleAsync(x => x.Purpose == OtpPurpose.BeneficiaryActivation)).Id;
        }

        var retry = await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(wallet.PublicHandle, "Lost response"));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var recovered = (await retry.Content.ReadFromJsonAsync<BeneficiaryResponse>())!;
        Assert.Equal(pendingBeneficiary, recovered.Id);
        Assert.Equal(issuedChallenge, await ChallengeIdAsync(recovered.Id));
        await using var finalScope = _factory.Services.CreateAsyncScope();
        Assert.Equal(1, await finalScope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Beneficiaries.CountAsync());
        Assert.Equal(1, await finalScope.ServiceProvider.GetRequiredService<IdentityDbContext>().OtpChallenges.CountAsync(x => x.Purpose == OtpPurpose.BeneficiaryActivation));
    }

    [Fact]
    public async Task Concurrent_verify_performs_one_logical_transition_without_corrupting_the_beneficiary()
    {
        var setup = await CreateBeneficiaryAsync("verify-race");
        var request = new VerifyBeneficiaryRequest(_factory.Otp.Get(setup.ChallengeId), setup.Beneficiary.ConcurrencyVersion);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/verify", request)));

        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Beneficiaries.SingleAsync();
        Assert.Equal(BeneficiaryStatus.CoolingOff, stored.Status);
        Assert.NotNull(stored.VerifiedAt);
        Assert.NotNull(stored.ActivateAfter);
        Assert.Null(stored.ActivatedAt);
        Assert.Null(stored.DeletedAt);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().OtpChallenges.CountAsync(x => x.Purpose == OtpPurpose.BeneficiaryActivation && x.UsedAt != null));
    }

    [Fact]
    public async Task Concurrent_activation_performs_one_transition_and_finishes_active()
    {
        var setup = await CreateBeneficiaryAsync("activate-race");
        var cooling = await VerifyAsync(setup);
        _factory.Time.Advance(TimeSpan.FromMinutes(31));
        var request = new ActivateBeneficiaryRequest(cooling.ConcurrencyVersion);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/activate", request)));

        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Beneficiaries.SingleAsync();
        Assert.Equal(BeneficiaryStatus.Active, stored.Status);
        Assert.NotNull(stored.ActivatedAt);
    }

    [Fact]
    public async Task Stale_concurrency_versions_in_verify_and_activate_return_concurrency_conflict()
    {
        var setup = await CreateBeneficiaryAsync("stale");
        var staleVerify = await setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/verify",
            new VerifyBeneficiaryRequest(_factory.Otp.Get(setup.ChallengeId), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, staleVerify.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", await ProblemCodeAsync(staleVerify));

        var cooling = await VerifyAsync(setup);
        _factory.Time.Advance(TimeSpan.FromMinutes(31));
        var staleActivate = await setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/activate", new ActivateBeneficiaryRequest(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, staleActivate.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", await ProblemCodeAsync(staleActivate));

        var activated = await setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/activate", new ActivateBeneficiaryRequest(cooling.ConcurrencyVersion));
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
    }

    [Fact]
    public async Task BOLA_hides_resend_get_verify_activate_and_delete_from_other_users()
    {
        var setup = await CreateBeneficiaryAsync("bola");
        using var other = _factory.CreateClient();
        var otherAuth = await AuthenticateAndActivateAsync(other, "bola-other@example.test", "bola-other", "Other");
        other.DefaultRequestHeaders.Authorization = new("Bearer", otherAuth.AccessToken);
        var responses = await Task.WhenAll(
            other.PostAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/otp", null),
            other.GetAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}"),
            other.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/verify", new VerifyBeneficiaryRequest("000000")),
            other.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/activate", new ActivateBeneficiaryRequest()),
            other.DeleteAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}"));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        foreach (var response in responses) Assert.Equal("BENEFICIARY_NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task List_does_not_expose_another_users_beneficiaries()
    {
        var setup = await CreateBeneficiaryAsync("list-isolation");
        using var other = _factory.CreateClient();
        var otherAuth = await AuthenticateAndActivateAsync(other, "list-other@example.test", "list-other", "Other");
        other.DefaultRequestHeaders.Authorization = new("Bearer", otherAuth.AccessToken);

        var page = (await (await other.GetAsync("/api/v1/beneficiaries")).Content.ReadFromJsonAsync<BeneficiaryPage>())!;
        Assert.Empty(page.Items);
        Assert.DoesNotContain(page.Items, item => item.Id == setup.Beneficiary.Id);
    }

    [Fact]
    public async Task Missing_and_ineligible_recipients_have_indistinguishable_not_found_responses()
    {
        using var owner = _factory.CreateClient();
        using var recipient = _factory.CreateClient();
        var ownerAuth = await AuthenticateAndActivateAsync(owner, "recipient-owner@example.test", "recipient-owner", "Owner");
        await AuthenticateAndActivateAsync(recipient, "recipient-wallet@example.test", "recipient-wallet", "Recipient");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerAuth.AccessToken);
        var wallet = await CreateWalletAsync(recipient);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var wallets = scope.ServiceProvider.GetRequiredService<WalletsDbContext>();
            (await wallets.Wallets.SingleAsync(x => x.Id == wallet.Id)).Status = WalletStatus.Frozen;
            await wallets.SaveChangesAsync();
        }

        var missing = await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest("w_not_a_real_recipient", "Missing"));
        var ineligible = await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(wallet.PublicHandle, "Ineligible"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(missing.StatusCode, ineligible.StatusCode);
        Assert.Equal("RECIPIENT_NOT_FOUND", await ProblemCodeAsync(missing));
        Assert.Equal(await ProblemCodeAsync(missing), await ProblemCodeAsync(ineligible));
    }

    [Fact]
    public async Task Target_wallet_id_is_immutable_after_beneficiary_creation()
    {
        var setup = await CreateBeneficiaryAsync("immutable-target");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>();
        var stored = await db.Beneficiaries.SingleAsync(x => x.Id == setup.Beneficiary.Id);
        db.Entry(stored).Property(x => x.TargetWalletId).CurrentValue = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(setup.Wallet.Id, (await db.Beneficiaries.SingleAsync(x => x.Id == setup.Beneficiary.Id)).TargetWalletId);
    }

    [Fact]
    public async Task Keyset_pagination_is_stable_complete_validates_cursor_and_caps_page_size()
    {
        using var owner = _factory.CreateClient();
        var ownerAuth = await AuthenticateAndActivateAsync(owner, "page-owner@example.test", "page-owner", "Owner");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerAuth.AccessToken);
        var ownerId = UserId(ownerAuth);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            var customer = await customers.Customers.SingleAsync(x => x.IdentityUserId == ownerId);
            var db = scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>();
            var now = _factory.Time.GetUtcNow();
            db.Beneficiaries.AddRange(Enumerable.Range(0, 7).Select(i => Beneficiary.Create(customer.Id, Guid.NewGuid(), $"Page {i}", "R***", now.AddSeconds(i))));
            await db.SaveChangesAsync();
        }

        var first = await PageAsync(owner, null, 3);
        var second = await PageAsync(owner, first.NextCursor, 3);
        var third = await PageAsync(owner, second.NextCursor, 3);
        var ids = first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.Id).ToList();
        Assert.Equal(7, ids.Count);
        Assert.Equal(7, ids.Distinct().Count());
        var all = ids.Select((id, index) => new { id, index }).ToDictionary(x => x.id, x => x.index);
        Assert.Equal(ids.OrderByDescending(id => first.Items.Concat(second.Items).Concat(third.Items).Single(x => x.Id == id).CreatedAt).ThenByDescending(id => id), ids);
        Assert.Null(third.NextCursor);

        var invalid = await owner.GetAsync("/api/v1/beneficiaries?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ProblemCodeAsync(invalid));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            var customer = await customers.Customers.SingleAsync(x => x.IdentityUserId == ownerId);
            var db = scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>();
            var now = _factory.Time.GetUtcNow().AddHours(1);
            db.Beneficiaries.AddRange(Enumerable.Range(0, 100).Select(i => Beneficiary.Create(customer.Id, Guid.NewGuid(), $"Cap {i}", "R***", now.AddSeconds(i))));
            await db.SaveChangesAsync();
        }
        var capped = await PageAsync(owner, null, 999);
        Assert.Equal(100, capped.Items.Count);
    }

    [Fact]
    public async Task PostgreSql_rejects_inconsistent_beneficiary_state_and_dates_with_check_constraints()
    {
        var setup = await CreateBeneficiaryAsync("constraints");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>();
        var state = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE beneficiaries.beneficiaries SET \"Status\" = 'Active' WHERE \"Id\" = {setup.Beneficiary.Id}"));
        Assert.Equal("23514", state.SqlState);
        var dates = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE beneficiaries.beneficiaries SET \"UpdatedAt\" = \"CreatedAt\" - INTERVAL '1 second' WHERE \"Id\" = {setup.Beneficiary.Id}"));
        Assert.Equal("23514", dates.SqlState);
    }

    [Theory]
    [InlineData("Beneficiaries:CoolingPeriodMinutes", "-1")]
    [InlineData("Beneficiaries:MaxPageSize", "0")]
    public async Task Invalid_beneficiary_configuration_prevents_application_startup(string key, string value)
    {
        await using var invalidFactory = new IdentityPostgresFactory(_factory.Services.GetRequiredService<BeneficiariesDbContext>().Database.GetConnectionString()!, new Dictionary<string, string?> { [key] = value });
        var error = await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            using var client = invalidFactory.CreateClient();
            await client.GetAsync("/health");
        });
        Assert.Contains(key.Split(':')[1], error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Beneficiary_rate_limits_partition_authenticated_users_by_sub_and_identity_anonymous_requests_by_ip()
    {
        using var anonymousA = _factory.CreateClient();
        using var anonymousB = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousA.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("unknown@example.test", "Password!12345", new DeviceRequest("anonymous", "Anonymous")))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymousB.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("unknown@example.test", "Password!12345", new DeviceRequest("anonymous", "Anonymous")))).StatusCode);

        using var first = _factory.CreateClient();
        using var second = _factory.CreateClient();
        var firstAuth = await AuthenticateAndActivateAsync(first, "rate-first@example.test", "rate-first", "First", useRegistrationSession: true);
        var secondAuth = await AuthenticateAndActivateAsync(second, "rate-second@example.test", "rate-second", "Second", useRegistrationSession: true);
        first.DefaultRequestHeaders.Authorization = new("Bearer", firstAuth.AccessToken);
        second.DefaultRequestHeaders.Authorization = new("Bearer", secondAuth.AccessToken);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await first.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest($"w_missing_{i}", "Rate"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await first.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest("w_missing_limit", "Rate"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await second.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest("w_missing_other", "Rate"))).StatusCode);
    }

    private async Task<BeneficiarySetup> CreateBeneficiaryAsync(string prefix)
    {
        var owner = _factory.CreateClient();
        var recipient = _factory.CreateClient();
        var ownerAuth = await AuthenticateAndActivateAsync(owner, $"{prefix}-owner@example.test", $"{prefix}-owner", "Owner");
        await AuthenticateAndActivateAsync(recipient, $"{prefix}-recipient@example.test", $"{prefix}-recipient", "Recipient");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerAuth.AccessToken);
        var wallet = await CreateWalletAsync(recipient);
        var added = await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(wallet.PublicHandle, "Acceptance"));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var beneficiary = (await added.Content.ReadFromJsonAsync<BeneficiaryResponse>())!;
        return new BeneficiarySetup(owner, recipient, ownerAuth, wallet, beneficiary, await ChallengeIdAsync(beneficiary.Id));
    }

    private async Task<BeneficiaryResponse> VerifyAsync(BeneficiarySetup setup)
    {
        var response = await setup.Owner.PostAsJsonAsync($"/api/v1/beneficiaries/{setup.Beneficiary.Id}/verify", new VerifyBeneficiaryRequest(_factory.Otp.Get(setup.ChallengeId), setup.Beneficiary.ConcurrencyVersion));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BeneficiaryResponse>())!;
    }

    private async Task<BeneficiaryPage> PageAsync(HttpClient client, string? cursor, int size)
    {
        var suffix = cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
        var response = await client.GetAsync($"/api/v1/beneficiaries?pageSize={size}{suffix}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BeneficiaryPage>())!;
    }

    private async Task<AuthResult> AuthenticateAndActivateAsync(HttpClient client, string email, string device, string firstName, bool useRegistrationSession = false)
    {
        var auth = await RegisterAndLoginAsync(client, email, device, useRegistrationSession);
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        var profile = await client.PostAsJsonAsync("/api/v1/me/profile", new CreateProfileRequest(firstName, "Acceptance", "+966" + Random.Shared.Next(100000000, 999999999), "ar-SA"));
        Assert.Equal(HttpStatusCode.Created, profile.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        var customer = await customers.Customers.SingleAsync(x => x.IdentityUserId == UserId(auth));
        customer.Status = CustomerStatus.Active;
        customer.KycStatus = KycStatus.Verified;
        await customers.SaveChangesAsync();
        return auth;
    }

    private async Task<AuthResult> RegisterAndLoginAsync(HttpClient client, string email, string device, bool useRegistrationSession = false)
    {
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Password!12345", true, "ar-SA", new DeviceRequest(device, "Acceptance")));
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var challenge = (await register.Content.ReadFromJsonAsync<ChallengeResponse>())!.ChallengeId;
        var verify = await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, _factory.Otp.Get(challenge), new DeviceRequest(device, "Acceptance")));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        if (useRegistrationSession) return (await verify.Content.ReadFromJsonAsync<AuthResult>())!;
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Password!12345", new DeviceRequest(device, "Acceptance")));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<AuthResult>())!;
    }

    private static async Task<WalletResponse> CreateWalletAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WalletResponse>())!;
    }

    private async Task<Guid> ChallengeIdAsync(Guid beneficiaryId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Beneficiaries.SingleAsync(x => x.Id == beneficiaryId)).OtpChallengeId!.Value;
    }

    private static Guid UserId(AuthResult auth) => Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken).Subject);
    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed record ChallengeResponse(Guid ChallengeId);
    private sealed record BeneficiarySetup(HttpClient Owner, HttpClient Recipient, AuthResult OwnerAuth, WalletResponse Wallet, BeneficiaryResponse Beneficiary, Guid ChallengeId) : IDisposable
    {
        public void Dispose() { Owner.Dispose(); Recipient.Dispose(); }
    }
}
