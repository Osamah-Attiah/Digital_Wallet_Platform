using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Authorization;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class IdentityPostgresTests : IClassFixture<IdentityDatabaseFixture>, IAsyncLifetime
{
    private readonly IdentityPostgresFactory _factory;
    public IdentityPostgresTests(IdentityDatabaseFixture database) => _factory = new IdentityPostgresFactory(database.ConnectionString);

    public async Task InitializeAsync() => await _factory.ResetAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Register_verify_login_and_rotation_are_secure()
    {
        using var client = _factory.CreateClient();
        var email = "person@example.test";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Password!12345", true, "ar-SA", new DeviceRequest("phone-1", "iPhone")));
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var challenge = (await register.Content.ReadFromJsonAsync<ChallengeResponse>())!.ChallengeId;
        var otp = _factory.Otp.Get(challenge);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, otp, new DeviceRequest("phone-1", "iPhone")))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Password!12345", new DeviceRequest("phone-1", "iPhone")));
        var auth = await login.Content.ReadFromJsonAsync<AuthResult>();
        Assert.NotNull(auth);
        var rotated = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth!.RefreshToken));
        var refreshed = await rotated.Content.ReadFromJsonAsync<AuthResult>();
        Assert.NotEqual(auth.RefreshToken, refreshed!.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshed.RefreshToken))).StatusCode);
        var freshLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Password!12345", new DeviceRequest("phone-1", "iPhone")));
        var fresh = (await freshLogin.Content.ReadFromJsonAsync<AuthResult>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", fresh.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout", new RefreshRequest(fresh.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(fresh.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Otp_rejects_wrong_expired_and_already_used_values()
    {
        using var client = _factory.CreateClient();
        var challenge = await RegisterAsync(client, "otp@example.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, _factory.Otp.Get(challenge) == "000000" ? "111111" : "000000", new DeviceRequest("a", "A")))).StatusCode);
        var code = _factory.Otp.Get(challenge);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, code, new DeviceRequest("a", "A")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, code, new DeviceRequest("a", "A")))).StatusCode);
        var expired = await RegisterAsync(client, "expired@example.test");
        await _factory.ExpireAsync(expired);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(expired, _factory.Otp.Get(expired), new DeviceRequest("b", "B")))).StatusCode);
    }

    [Fact]
    public async Task Logout_all_password_reset_and_device_ownership_are_enforced()
    {
        using var client = _factory.CreateClient();
        var first = await VerifiedAuthAsync(client, "first@example.test", "one");
        var second = await VerifiedAuthAsync(client, "second@example.test", "two");
        client.DefaultRequestHeaders.Authorization = new("Bearer", first.AccessToken);
        var devices = await client.GetFromJsonAsync<List<DeviceResponse>>("/api/v1/devices");
        Assert.NotNull(devices); Assert.Single(devices!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/devices/{second.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout-all", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken))).StatusCode);
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("second@example.test"));
        var reset = await _factory.LatestChallengeAsync("second@example.test", OtpPurpose.PasswordReset);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(reset, _factory.Otp.Get(reset), "Changed!Password123"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Permission_allow_deny_fallback_and_postgres_unique_constraints_work()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "p", NormalizedUserName = "P", Email = "p@test", NormalizedEmail = "P@TEST", Contact = "p@test", NormalizedContact = "P@TEST", EmailConfirmed = true };
        db.Users.Add(user); await db.SaveChangesAsync();
        var role = new ApplicationRole { Id = Guid.NewGuid(), Name = "Reader", NormalizedName = "READER", DisplayName = "Reader" };
        db.Roles.Add(role); db.UserRoles.Add(new ApplicationUserRole { UserId = user.Id, RoleId = role.Id }); db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = "profile.view.own" }); await db.SaveChangesAsync();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        Assert.True(await resolver.HasAsync(user.Id, "profile.view.own", default));
        Assert.False(await resolver.HasAsync(user.Id, "wallet.freeze", default));
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.Id.ToString())], "test"));
        Assert.True((await authorization.AuthorizeAsync(principal, null, Permissions.Prefix + "profile.view.own")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(principal, null, Permissions.Prefix + "wallet.freeze")).Succeeded);
        db.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserName = "duplicate", NormalizedUserName = "DUPLICATE", Contact = "other@test", NormalizedContact = "P@TEST", Email = "other@test", NormalizedEmail = "OTHER@TEST" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("missing@example.test", "Password!12345", new DeviceRequest("missing", "Missing")));
        var error = await login.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.DoesNotContain("missing@example.test", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password!12345", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_refresh_requests_create_only_one_successor_and_replay_revokes_the_family()
    {
        using var client = _factory.CreateClient();
        var auth = await VerifiedAuthAsync(client, "race@example.test", "race");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var owner = await db.RefreshTokens.Where(x => x.FamilyId == auth.SessionId).Select(x => x.UserId).SingleAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM identity.users WHERE \"Id\" = {owner} FOR UPDATE");
        // Hold the production lock until all eight independent HTTP requests have reached
        // PostgreSQL. This tests actual overlap, rather than relying on task scheduling.
        var requests = Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken))).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var waiting = 0;
        while (waiting < requests.Length && DateTime.UtcNow < deadline)
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_stat_clear_snapshot()");
            waiting = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%identity.users%'").SingleAsync();
            if (waiting < requests.Length) await Task.Delay(20);
        }
        await transaction.CommitAsync();
        var responses = await Task.WhenAll(requests);
        Assert.Equal(8, waiting);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, responses.Count(x => x.StatusCode == HttpStatusCode.Unauthorized));
        var winner = (await responses.Single(x => x.IsSuccessStatusCode).Content.ReadFromJsonAsync<AuthResult>())!;
        var family = await db.RefreshTokens.AsNoTracking().Where(x => x.FamilyId == auth.SessionId).ToListAsync();
        Assert.Equal(2, family.Count);
        Assert.Equal(1, family.Count(x => x.UsedAt != null));
        Assert.All(family, token => Assert.NotNull(token.RevokedAt));
        Assert.Equal(winner.SessionId, auth.SessionId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(winner.RefreshToken))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", winner.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);
    }

    [Theory]
    [InlineData("logout-all")]
    [InlineData("password-reset")]
    [InlineData("security-stamp")]
    [InlineData("lockout")]
    public async Task Security_changes_invalidate_existing_access_and_refresh_tokens(string change)
    {
        using var client = _factory.CreateClient();
        var first = await VerifiedAuthAsync(client, "security@example.test", "first");
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("security@example.test", "Password!12345", new DeviceRequest("second", "Second")));
        var second = (await login.Content.ReadFromJsonAsync<AuthResult>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", first.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/devices")).StatusCode);
        switch (change)
        {
            case "logout-all":
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout-all", new { })).StatusCode);
                break;
            case "password-reset":
                await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("security@example.test"));
                var challenge = await _factory.LatestChallengeAsync("security@example.test", OtpPurpose.PasswordReset);
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(challenge, _factory.Otp.Get(challenge), "Changed!Password123"))).StatusCode);
                break;
            default:
                await _factory.InDatabaseAsync(async db =>
                {
                    var user = await db.Users.SingleAsync();
                    if (change == "security-stamp") user.SecurityStamp = Guid.NewGuid().ToString();
                    else { user.LockoutEnabled = true; user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1); }
                    await db.SaveChangesAsync();
                });
                break;
        }
        foreach (var session in new[] { first, second })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(session.RefreshToken))).StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = null;
        var freshLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("security@example.test", change == "password-reset" ? "Changed!Password123" : "Password!12345", new DeviceRequest("new", "New")));
        Assert.Equal(change == "lockout" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, freshLogin.StatusCode);
        if (change != "lockout")
        {
            var fresh = (await freshLogin.Content.ReadFromJsonAsync<AuthResult>())!;
            client.DefaultRequestHeaders.Authorization = new("Bearer", fresh.AccessToken);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/devices")).StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_device_cannot_refresh_even_if_its_token_row_is_still_active(bool revokeThroughApi)
    {
        using var client = _factory.CreateClient();
        var auth = await VerifiedAuthAsync(client, "device@example.test", "device");
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        if (revokeThroughApi)
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/devices/{auth.DeviceId}")).StatusCode);
        else
            await _factory.InDatabaseAsync(async db =>
            {
                (await db.Devices.SingleAsync()).RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                Assert.True(await db.RefreshTokens.AnyAsync(x => x.RevokedAt == null));
            });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken))).StatusCode);
        await _factory.InDatabaseAsync(async db => Assert.Equal(1, await db.RefreshTokens.CountAsync()));
    }

    [Fact]
    public async Task Normal_rotation_keeps_access_valid_and_logout_only_revokes_its_own_family()
    {
        using var client = _factory.CreateClient();
        var first = await VerifiedAuthAsync(client, "sessions@example.test", "first");
        var second = (await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("sessions@example.test", "Password!12345", new DeviceRequest("second", "Second")))).Content.ReadFromJsonAsync<AuthResult>())!;
        var rotated = (await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken))).Content.ReadFromJsonAsync<AuthResult>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", first.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout", new RefreshRequest(rotated.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", second.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/devices")).StatusCode);
    }

    [Fact]
    public async Task Otp_maximum_attempts_rejects_the_correct_code_after_the_limit()
    {
        using var client = _factory.CreateClient();
        var challenge = await RegisterAsync(client, "attempts@example.test");
        var code = _factory.Otp.Get(challenge);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, code == "000000" ? "111111" : "000000", new DeviceRequest("a", "A")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, code, new DeviceRequest("a", "A")))).StatusCode);
        await _factory.InDatabaseAsync(async db =>
        {
            var stored = await db.OtpChallenges.SingleAsync();
            Assert.Equal(5, stored.AttemptCount);
            Assert.Null(stored.UsedAt);
            Assert.Empty(await db.RefreshTokens.ToListAsync());
        });
    }

    [Fact]
    public async Task Otp_resend_cooldown_superseding_and_resend_limit_are_enforced()
    {
        using var client = _factory.CreateClient();
        await VerifiedAuthAsync(client, "resend@example.test", "resend");
        async Task Resend()
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IIdentityService>().ForgotPasswordAsync(new ForgotPasswordRequest("resend@example.test"), default);
        }
        await Resend();
        var first = await _factory.LatestChallengeAsync("resend@example.test", OtpPurpose.PasswordReset);
        var sent = _factory.Otp.SentCount;
        await Resend();
        Assert.Equal(first, await _factory.LatestChallengeAsync("resend@example.test", OtpPurpose.PasswordReset));
        Assert.Equal(sent, _factory.Otp.SentCount);
        async Task Age(Guid id) => await _factory.InDatabaseAsync(async db =>
        {
            // Use the same controlled clock as Identity; wall-clock reset/migration time must not shorten the cooldown age.
            (await db.OtpChallenges.SingleAsync(x => x.Id == id)).CreatedAt = _factory.Time.GetUtcNow().AddSeconds(-61);
            await db.SaveChangesAsync();
        });
        await Age(first);
        await Resend();
        var second = await _factory.LatestChallengeAsync("resend@example.test", OtpPurpose.PasswordReset);
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(first, _factory.Otp.Get(first), "Changed!Password123"))).StatusCode);
        await Age(second);
        await Resend();
        var third = await _factory.LatestChallengeAsync("resend@example.test", OtpPurpose.PasswordReset);
        await Age(third);
        await Resend();
        Assert.Equal(sent + 2, _factory.Otp.SentCount);
        await _factory.InDatabaseAsync(async db =>
        {
            var challenges = await db.OtpChallenges.Where(x => x.Purpose == OtpPurpose.PasswordReset).ToListAsync();
            Assert.Equal(3, challenges.Count);
            Assert.Single(challenges, x => x.SupersededAt == null);
        });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(third, _factory.Otp.Get(third), "Changed!Password123"))).StatusCode);
    }

    [Theory]
    [InlineData(OtpPurpose.Registration)]
    [InlineData(OtpPurpose.PasswordReset)]
    public async Task Otp_is_bound_to_its_purpose(OtpPurpose purpose)
    {
        using var client = _factory.CreateClient();
        Guid challenge;
        if (purpose == OtpPurpose.Registration) challenge = await RegisterAsync(client, "purpose@example.test");
        else
        {
            await VerifiedAuthAsync(client, "purpose@example.test", "purpose");
            await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("purpose@example.test"));
            challenge = await _factory.LatestChallengeAsync("purpose@example.test", purpose);
        }
        var response = purpose == OtpPurpose.Registration
            ? await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(challenge, _factory.Otp.Get(challenge), "Changed!Password123"))
            : await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, _factory.Otp.Get(challenge), new DeviceRequest("a", "A")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _factory.InDatabaseAsync(async db =>
        {
            var stored = await db.OtpChallenges.SingleAsync(x => x.Id == challenge);
            Assert.Null(stored.UsedAt);
            Assert.Equal(0, stored.AttemptCount);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Password_reset_otp_rejects_expiry_and_reuse(bool expire)
    {
        using var client = _factory.CreateClient();
        await VerifiedAuthAsync(client, "reset-otp@example.test", "reset");
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("reset-otp@example.test"));
        var challenge = await _factory.LatestChallengeAsync("reset-otp@example.test", OtpPurpose.PasswordReset);
        var reset = new ResetPasswordRequest(challenge, _factory.Otp.Get(challenge), "Changed!Password123");
        if (expire) await _factory.ExpireAsync(challenge);
        else Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", reset)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", reset)).StatusCode);
    }

    [Theory]
    [InlineData("login", 5, HttpStatusCode.Unauthorized)]
    [InlineData("register/verify", 8, HttpStatusCode.BadRequest)]
    [InlineData("refresh", 20, HttpStatusCode.Unauthorized)]
    [InlineData("forgot-password", 3, HttpStatusCode.Accepted)]
    [InlineData("reset-password", 3, HttpStatusCode.BadRequest)]
    public async Task Sensitive_routes_enforce_their_real_rate_limits(string route, int limit, HttpStatusCode permittedStatus)
    {
        using var client = _factory.CreateClient();
        object request = route switch
        {
            "login" => new LoginRequest("missing@example.test", "Password!12345", new DeviceRequest("a", "A")),
            "register/verify" => new VerifyOtpRequest(Guid.NewGuid(), "123456", new DeviceRequest("a", "A")),
            "refresh" => new RefreshRequest("invalid-test-token"),
            "forgot-password" => new ForgotPasswordRequest("missing@example.test"),
            _ => new ResetPasswordRequest(Guid.NewGuid(), "123456", "Changed!Password123")
        };
        for (var i = 0; i < limit; i++)
            Assert.Equal(permittedStatus, (await client.PostAsJsonAsync($"/api/v1/auth/{route}", request)).StatusCode);
        var rejected = await client.PostAsJsonAsync($"/api/v1/auth/{route}", request);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Initialize_runs_real_migrations_and_seed_and_is_idempotent()
    {
        await _factory.Services.InitializeIdentityModuleAsync();
        await _factory.Services.InitializeIdentityModuleAsync();
        await _factory.InDatabaseAsync(async db =>
        {
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains("20260910131128_InitialIdentity", await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(Permissions.All.Order(), (await db.Permissions.Select(x => x.Code).ToListAsync()).Order());
            var customer = await db.Roles.SingleAsync(x => x.NormalizedName == "CUSTOMER");
            var grants = await db.RolePermissions.Where(x => x.RoleId == customer.Id).Select(x => x.PermissionCode).ToListAsync();
            Assert.Equal(new[] { "profile.view.own", "profile.edit.own", "wallet.create.own", "wallet.view.own", "beneficiary.manage.own", "transfer.create", "transfer.view.own", "transaction.view.own", "notification.manage.own" }.Order(), grants.Order());
            Assert.DoesNotContain("wallet.freeze", grants);
            Assert.Empty(await db.Users.ToListAsync());
            Assert.All(db.Model.GetEntityTypes(), entity => Assert.Equal("identity", entity.GetSchema()));
        });
    }

    [Fact]
    public async Task Replay_of_an_ancestor_racing_with_descendant_rotation_revokes_every_descendant()
    {
        using var client = _factory.CreateClient();
        var original = await VerifiedAuthAsync(client, "descendant@example.test", "race");
        var next = (await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(original.RefreshToken))).Content.ReadFromJsonAsync<AuthResult>())!;
        var results = await Task.WhenAll(
            client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(original.RefreshToken)),
            client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(next.RefreshToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, results[0].StatusCode);
        Assert.Contains(results[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
        await _factory.InDatabaseAsync(async db =>
        {
            var family = await db.RefreshTokens.Where(x => x.FamilyId == original.SessionId).ToListAsync();
            Assert.InRange(family.Count, 2, 3);
            Assert.All(family, x => Assert.NotNull(x.RevokedAt));
        });
        if (results[1].IsSuccessStatusCode)
        {
            var last = (await results[1].Content.ReadFromJsonAsync<AuthResult>())!;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(last.RefreshToken))).StatusCode);
        }
    }

    [Fact]
    public async Task Password_reset_otp_maximum_attempts_are_persisted()
    {
        using var client = _factory.CreateClient();
        await VerifiedAuthAsync(client, "reset-attempts@example.test", "reset");
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("reset-attempts@example.test"));
        var challenge = await _factory.LatestChallengeAsync("reset-attempts@example.test", OtpPurpose.PasswordReset);
        var code = _factory.Otp.Get(challenge);
        // Exercise the OTP policy independently of the stricter HTTP password limiter.
        for (var i = 0; i < 6; i++)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IIdentityService>();
            var attempt = i == 5 ? code : code == "000000" ? "111111" : "000000";
            Assert.False(await service.ResetPasswordAsync(new ResetPasswordRequest(challenge, attempt, "Changed!Password123"), default));
        }
        await _factory.InDatabaseAsync(async db =>
        {
            var stored = await db.OtpChallenges.SingleAsync(x => x.Id == challenge);
            Assert.Equal(5, stored.AttemptCount);
            Assert.Null(stored.UsedAt);
        });
    }

    [Fact]
    public async Task Concurrent_otp_verification_is_single_use()
    {
        using var client = _factory.CreateClient();
        var challenge = await RegisterAsync(client, "otp-race@example.test");
        var request = new VerifyOtpRequest(challenge, _factory.Otp.Get(challenge), new DeviceRequest("race", "Race"));
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync("/api/v1/auth/register/verify", request)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, responses.Count(x => x.StatusCode == HttpStatusCode.BadRequest));
        await _factory.InDatabaseAsync(async db =>
        {
            Assert.Equal(1, await db.RefreshTokens.CountAsync());
            Assert.Equal(1, (await db.OtpChallenges.SingleAsync()).AttemptCount);
        });
    }

    [Fact]
    public async Task Forgot_and_reset_password_share_the_same_rate_limit()
    {
        using var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("missing@example.test"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(Guid.NewGuid(), "123456", "Changed!Password123"))).StatusCode);
    }

    private async Task<Guid> RegisterAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Password!12345", true, "ar-SA", new DeviceRequest(Guid.NewGuid().ToString("N"), "Test")));
        return (await response.Content.ReadFromJsonAsync<ChallengeResponse>())!.ChallengeId;
    }
    private async Task<AuthResult> VerifiedAuthAsync(HttpClient client, string email, string device)
    {
        var challenge = await RegisterAsync(client, email);
        var verify = await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, _factory.Otp.Get(challenge), new DeviceRequest(device, device)));
        return (await verify.Content.ReadFromJsonAsync<AuthResult>())!;
    }
    private sealed record ChallengeResponse(Guid ChallengeId);
}
