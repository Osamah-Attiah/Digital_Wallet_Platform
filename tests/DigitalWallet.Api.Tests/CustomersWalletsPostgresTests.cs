using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Customers.Services;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using DigitalWallet.Wallets.Persistence;
using DigitalWallet.Wallets.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class CustomersWalletsPostgresTests : IClassFixture<IdentityDatabaseFixture>, IAsyncLifetime
{
    private readonly IdentityPostgresFactory _factory;
    public CustomersWalletsPostgresTests(IdentityDatabaseFixture fixture) => _factory = new IdentityPostgresFactory(fixture.ConnectionString);
    public Task InitializeAsync() => _factory.ResetAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Customer_wallet_lifecycle_ownership_masking_and_concurrent_provisioning_use_postgres()
    {
        using var a = _factory.CreateClient(); using var b = _factory.CreateClient(); using var admin = _factory.CreateClient();
        var aAuth = await AuthAsync(a, "a@example.test", "a"); var bAuth = await AuthAsync(b, "b@example.test", "b"); var adminAuth = await AuthAsync(admin, "admin@example.test", "admin");
        a.DefaultRequestHeaders.Authorization = new("Bearer", aAuth.AccessToken); b.DefaultRequestHeaders.Authorization = new("Bearer", bAuth.AccessToken); admin.DefaultRequestHeaders.Authorization = new("Bearer", adminAuth.AccessToken);
        var aProfile = await CreateProfileAsync(a, "Amina", "Private", "+966500000001");
        var bProfile = await CreateProfileAsync(b, "Badr", "Private", "+966500000002");
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/v1/me/profile", new CreateProfileRequest("Again", "No", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/v1/admin/customers/{aProfile.Id}")).StatusCode);
        await GrantAsync(adminAuth, "customer.view");
        var masked = await admin.GetFromJsonAsync<CustomerResponse>($"/api/v1/admin/customers/{aProfile.Id}"); Assert.NotNull(masked); Assert.NotEqual("Amina", masked!.FirstName);
        await GrantAsync(adminAuth, "customer.pii.view", "customer.suspend", "customer.block", "customer.unblock", "kyc.review", "wallet.freeze", "wallet.unfreeze");
        var own = await a.GetFromJsonAsync<CustomerResponse>("/api/v1/me"); Assert.Equal("Amina", own!.FirstName);
        var updatedResponse = await a.PatchAsJsonAsync("/api/v1/me", new UpdateProfileRequest("Amal", null, null, null, own.ConcurrencyVersion));
        var updated = await updatedResponse.Content.ReadFromJsonAsync<CustomerResponse>(); Assert.Equal("Amal", updated!.FirstName);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PatchAsJsonAsync("/api/v1/me", new UpdateProfileRequest("Race", null, null, null, own.ConcurrencyVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest())).StatusCode);
        var submitted = await a.PostAsJsonAsync("/api/v1/me/kyc/submit", new StateChangeRequest("MVP check", updated.ConcurrencyVersion));
        var kyc = await submitted.Content.ReadFromJsonAsync<KycResponse>(); Assert.Equal(DigitalWallet.Customers.Domain.KycStatus.Pending, kyc!.Status);
        var approved = await admin.PostAsJsonAsync($"/api/v1/admin/customers/{aProfile.Id}/kyc/approve", new StateChangeRequest("Approved", kyc.ConcurrencyVersion));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var requests = Enumerable.Range(0, 20).Select(_ => a.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest())).ToArray();
        var responses = await Task.WhenAll(requests); Assert.All(responses, x => Assert.Equal(HttpStatusCode.Created, x.StatusCode));
        var wallets = await a.GetFromJsonAsync<List<WalletResponse>>("/api/v1/wallets"); Assert.Single(wallets!);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/wallets/{wallets![0].Id}")).StatusCode);
        var mfa = await (await admin.PostAsJsonAsync("/api/v1/admin/session/mfa", new {})).Content.ReadFromJsonAsync<DigitalWallet.Identity.Contracts.OtpChallengeIssued>();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/admin/session/mfa/verify", new { challengeId=mfa!.ChallengeId, code=_factory.Otp.Get(mfa.ChallengeId) })).StatusCode);
        var frozen = await admin.PostAsJsonAsync($"/api/v1/admin/wallets/{wallets[0].Id}/freeze", new WalletStateChangeRequest("Review", wallets[0].ConcurrencyVersion));
        var frozenWallet = await frozen.Content.ReadFromJsonAsync<WalletResponse>(); Assert.Equal(DigitalWallet.Wallets.Domain.WalletStatus.Frozen, frozenWallet!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/admin/wallets/{wallets[0].Id}/unfreeze", new WalletStateChangeRequest("Stale", wallets[0].ConcurrencyVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/admin/wallets/{wallets[0].Id}/unfreeze", new WalletStateChangeRequest("Resolved", frozenWallet.ConcurrencyVersion))).StatusCode);
    }

    [Fact]
    public async Task Customers_and_wallets_migrations_apply_from_an_empty_database()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>(); var wallets = scope.ServiceProvider.GetRequiredService<WalletsDbContext>();
        Assert.Contains((await customers.Database.GetAppliedMigrationsAsync()), x => x.EndsWith("InitialCustomers", StringComparison.Ordinal));
        Assert.Contains((await wallets.Database.GetAppliedMigrationsAsync()), x => x.EndsWith("InitialWallets", StringComparison.Ordinal));
        Assert.False(customers.Database.HasPendingModelChanges()); Assert.False(wallets.Database.HasPendingModelChanges());
    }
    private async Task<AuthResult> AuthAsync(HttpClient client, string email, string device)
    {
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Password!12345", true, "ar-SA", new DeviceRequest(device, "Test")));
        var challenge = (await register.Content.ReadFromJsonAsync<ChallengeResponse>())!.ChallengeId;
        await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, _factory.Otp.Get(challenge), new DeviceRequest(device, "Test")));
        return (await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Password!12345", new DeviceRequest(device, "Test")))).Content.ReadFromJsonAsync<AuthResult>())!;
    }
    private static async Task<CustomerResponse> CreateProfileAsync(HttpClient client, string first, string last, string phone) => (await (await client.PostAsJsonAsync("/api/v1/me/profile", new CreateProfileRequest(first, last, phone, "ar-SA"))).Content.ReadFromJsonAsync<CustomerResponse>())!;
    private async Task GrantAsync(AuthResult auth, params string[] permissions)
    {
        await using var scope = _factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = await db.RefreshTokens.Where(x => x.FamilyId == auth.SessionId).Select(x => x.UserId).SingleAsync();
        var roleName = "StageThreeAdmin" + Guid.NewGuid().ToString("N");
        var role = new ApplicationRole { Id = Guid.NewGuid(), Name = roleName, NormalizedName = roleName.ToUpperInvariant(), DisplayName = roleName };
        db.Roles.Add(role); db.UserRoles.Add(new ApplicationUserRole { UserId = user, RoleId = role.Id });
        foreach (var permission in permissions) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = permission });
        await db.SaveChangesAsync();
    }
    private sealed record ChallengeResponse(Guid ChallengeId);
}
