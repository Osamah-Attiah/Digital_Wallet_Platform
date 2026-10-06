using System.Net;
using System.Net.Http.Json;
using DigitalWallet.Beneficiaries.Application;
using DigitalWallet.Beneficiaries.Domain;
using DigitalWallet.Beneficiaries.Infrastructure;
using DigitalWallet.Customers.Domain;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Customers.Services;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Identity.Services;
using DigitalWallet.Wallets.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class BeneficiariesPostgresTests : IClassFixture<IdentityDatabaseFixture>, IAsyncLifetime
{
    private readonly IdentityPostgresFactory _factory;
    public BeneficiariesPostgresTests(IdentityDatabaseFixture fixture) => _factory = new IdentityPostgresFactory(fixture.ConnectionString);
    public Task InitializeAsync() => _factory.ResetAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Beneficiary_migrations_apply_and_model_has_no_pending_changes()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>();
        Assert.Contains((await db.Database.GetAppliedMigrationsAsync()), x => x.EndsWith("InitialBeneficiaries", StringComparison.Ordinal));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Lifecycle_is_owned_bound_to_otp_cooled_and_soft_deleted()
    {
        using var owner = _factory.CreateClient();
        using var recipient = _factory.CreateClient();
        using var other = _factory.CreateClient();
        var ownerAuth = await AuthenticateAndActivateAsync(owner, "owner@example.test", "owner", "Owner");
        await AuthenticateAndActivateAsync(recipient, "recipient@example.test", "recipient", "Recipient");
        await AuthenticateAndActivateAsync(other, "other@example.test", "other", "Other");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerAuth.AccessToken);

        var recipientWallet = (await (await recipient.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest())).Content.ReadFromJsonAsync<WalletResponse>())!;
        var add = await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(recipientWallet.PublicHandle, "Rent"));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var beneficiary = (await add.Content.ReadFromJsonAsync<BeneficiaryResponse>())!;
        Assert.Equal(BeneficiaryStatus.PendingVerification, beneficiary.Status);
        Assert.NotEqual(recipientWallet.PublicHandle, beneficiary.MaskedRecipientName);
        Assert.Contains('*', beneficiary.MaskedRecipientName);
        var challengeId = await ChallengeIdAsync(beneficiary.Id);
        Assert.False((await add.Content.ReadAsStringAsync()).Contains("otpChallengeId", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(recipientWallet.PublicHandle, "Again"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await recipient.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(recipientWallet.PublicHandle, "Mine"))).StatusCode);

        other.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync(other, "other@example.test", "other")).AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/beneficiaries/{beneficiary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/beneficiaries/{beneficiary.Id}/verify", new VerifyBeneficiaryRequest("000000"))).StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var otp = scope.ServiceProvider.GetRequiredService<IOtpChallengeService>();
            var wrongTarget = await otp.VerifyAsync(new VerifyOtpChallenge(challengeId, UserId(ownerAuth), beneficiary.Id, Guid.NewGuid(), OtpChallengePurpose.BeneficiaryActivation, _factory.Otp.Get(challengeId)), default);
            Assert.Equal(OtpVerificationStatus.Invalid, wrongTarget);
            var wrongOwner = await otp.VerifyAsync(new VerifyOtpChallenge(challengeId, Guid.NewGuid(), beneficiary.Id, (await scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Beneficiaries.SingleAsync(x => x.Id == beneficiary.Id)).TargetWalletId, OtpChallengePurpose.BeneficiaryActivation, _factory.Otp.Get(challengeId)), default);
            var wrongBeneficiary = await otp.VerifyAsync(new VerifyOtpChallenge(challengeId, UserId(ownerAuth), Guid.NewGuid(), (await scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Beneficiaries.SingleAsync(x => x.Id == beneficiary.Id)).TargetWalletId, OtpChallengePurpose.BeneficiaryActivation, _factory.Otp.Get(challengeId)), default);
            Assert.Equal(OtpVerificationStatus.Invalid, wrongOwner);
            Assert.Equal(OtpVerificationStatus.Invalid, wrongBeneficiary);
        }
        var verify = await owner.PostAsJsonAsync($"/api/v1/beneficiaries/{beneficiary.Id}/verify", new VerifyBeneficiaryRequest(_factory.Otp.Get(challengeId), beneficiary.ConcurrencyVersion));
        var cooling = (await verify.Content.ReadFromJsonAsync<BeneficiaryResponse>())!;
        Assert.Equal(BeneficiaryStatus.CoolingOff, cooling.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/v1/beneficiaries/{beneficiary.Id}/activate", new ActivateBeneficiaryRequest(cooling.ConcurrencyVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/v1/beneficiaries/{beneficiary.Id}/verify", new VerifyBeneficiaryRequest(_factory.Otp.Get(challengeId)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/beneficiaries/{beneficiary.Id}/activate", new ActivateBeneficiaryRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/beneficiaries/{beneficiary.Id}")).StatusCode);

        _factory.Time.Advance(TimeSpan.FromMinutes(31));
        var activated = (await (await owner.PostAsJsonAsync($"/api/v1/beneficiaries/{beneficiary.Id}/activate", new ActivateBeneficiaryRequest())).Content.ReadFromJsonAsync<BeneficiaryResponse>())!;
        Assert.Equal(BeneficiaryStatus.Active, activated.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/v1/beneficiaries/{beneficiary.Id}?concurrencyVersion={cooling.ConcurrencyVersion}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/beneficiaries/{beneficiary.Id}?concurrencyVersion={activated.ConcurrencyVersion}")).StatusCode);
        var readded = await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(recipientWallet.PublicHandle, "Rent again"));
        Assert.Equal(HttpStatusCode.Created, readded.StatusCode);
    }

    [Fact]
    public async Task Add_retry_recovers_the_same_pending_beneficiary_after_identity_fails_before_otp_creation()
    {
        using var owner = _factory.CreateClient(); using var recipient = _factory.CreateClient();
        var ownerAuth = await AuthenticateAndActivateAsync(owner, "recover-owner@example.test", "recover-owner", "Owner");
        await AuthenticateAndActivateAsync(recipient, "recover-recipient@example.test", "recover-recipient", "Recipient");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerAuth.AccessToken);
        var wallet = (await (await recipient.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest())).Content.ReadFromJsonAsync<WalletResponse>())!;
        _factory.FailOtpCreateRequests = 1;
        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(wallet.PublicHandle, "Recovery"))).StatusCode);
        Guid pendingId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>();
            var pending = await db.Beneficiaries.SingleAsync();
            pendingId = pending.Id; Assert.Null(pending.OtpChallengeId);
        }
        var retry = await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(wallet.PublicHandle, "Recovery"));
        var recovered = (await retry.Content.ReadFromJsonAsync<BeneficiaryResponse>())!;
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(pendingId, recovered.Id);
        Assert.NotEqual(Guid.Empty, await ChallengeIdAsync(recovered.Id));
    }

    [Fact]
    public async Task Consumed_otp_receipt_recovers_a_failed_local_transition_and_cannot_be_replayed_afterwards()
    {
        using var owner = _factory.CreateClient(); using var recipient = _factory.CreateClient();
        var ownerAuth = await AuthenticateAndActivateAsync(owner, "receipt-owner@example.test", "receipt-owner", "Owner");
        await AuthenticateAndActivateAsync(recipient, "receipt-recipient@example.test", "receipt-recipient", "Recipient");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerAuth.AccessToken);
        var wallet = (await (await recipient.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest())).Content.ReadFromJsonAsync<WalletResponse>())!;
        var beneficiary = (await (await owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(wallet.PublicHandle, "Receipt"))).Content.ReadFromJsonAsync<BeneficiaryResponse>())!;
        var challengeId = await ChallengeIdAsync(beneficiary.Id);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var otp = scope.ServiceProvider.GetRequiredService<IOtpChallengeService>();
            Assert.Equal(OtpVerificationStatus.Verified, await otp.VerifyAsync(new(challengeId, UserId(ownerAuth), beneficiary.Id,
                (await scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Beneficiaries.SingleAsync(x => x.Id == beneficiary.Id)).TargetWalletId,
                OtpChallengePurpose.BeneficiaryActivation, _factory.Otp.Get(challengeId)), default));
        }
        var recovered = await owner.PostAsJsonAsync($"/api/v1/beneficiaries/{beneficiary.Id}/verify", new VerifyBeneficiaryRequest("000000"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(BeneficiaryStatus.CoolingOff, (await recovered.Content.ReadFromJsonAsync<BeneficiaryResponse>())!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/v1/beneficiaries/{beneficiary.Id}/verify", new VerifyBeneficiaryRequest(_factory.Otp.Get(challengeId)))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_add_requests_are_idempotent_and_create_one_live_beneficiary()
    {
        using var owner = _factory.CreateClient(); using var recipient = _factory.CreateClient();
        var ownerAuth = await AuthenticateAndActivateAsync(owner, "race-owner@example.test", "race-owner", "Owner");
        await AuthenticateAndActivateAsync(recipient, "race-recipient@example.test", "race-recipient", "Recipient");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerAuth.AccessToken);
        var wallet = (await (await recipient.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest())).Content.ReadFromJsonAsync<WalletResponse>())!;
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => owner.PostAsJsonAsync("/api/v1/beneficiaries", new AddBeneficiaryRequest(wallet.PublicHandle, "Race"))));
        // All requests use the same authenticated subject. The first ten exercise the
        // idempotent database path; the remaining requests are correctly stopped by
        // the production lookup rate limit before they can create another aggregate.
        Assert.Equal(10, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        Assert.Equal(10, responses.Count(x => x.StatusCode == HttpStatusCode.TooManyRequests));
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Beneficiaries.CountAsync());
    }

    private async Task<AuthResult> AuthenticateAndActivateAsync(HttpClient client, string email, string device, string firstName)
    {
        var auth = await RegisterAndLoginAsync(client, email, device);
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        var profile = await client.PostAsJsonAsync("/api/v1/me/profile", new CreateProfileRequest(firstName, "Test", "+966" + Random.Shared.Next(100000000, 999999999), "ar-SA"));
        Assert.Equal(HttpStatusCode.Created, profile.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        var customer = await customers.Customers.SingleAsync(x => x.IdentityUserId == UserId(auth));
        customer.Status = CustomerStatus.Active; customer.KycStatus = KycStatus.Verified;
        await customers.SaveChangesAsync();
        return auth;
    }

    private async Task<AuthResult> LoginAsync(HttpClient client, string email, string device) =>
        (await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Password!12345", new DeviceRequest(device, "Test")))).Content.ReadFromJsonAsync<AuthResult>())!;

    private async Task<AuthResult> RegisterAndLoginAsync(HttpClient client, string email, string device)
    {
        var registered = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Password!12345", true, "ar-SA", new DeviceRequest(device, "Test")));
        var challenge = (await registered.Content.ReadFromJsonAsync<ChallengeResponse>())!.ChallengeId;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, _factory.Otp.Get(challenge), new DeviceRequest(device, "Test")))).StatusCode);
        return await LoginAsync(client, email, device);
    }

    private static Guid UserId(AuthResult auth) => Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken).Subject);
    private async Task<Guid> ChallengeIdAsync(Guid beneficiaryId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Beneficiaries.SingleAsync(x => x.Id == beneficiaryId)).OtpChallengeId!.Value;
    }
    private sealed record ChallengeResponse(Guid ChallengeId);
}
