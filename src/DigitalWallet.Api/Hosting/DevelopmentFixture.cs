using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DigitalWallet.Identity.Services;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Authorization;
using DigitalWallet.Identity;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Customers.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace DigitalWallet.Api.Hosting;

public static class DevelopmentFixture
{
    public static void Configure(WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("DevelopmentFixtures:Enabled")) return;
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("Fixtures require Development or Testing.");
        app.MapPost("/api/dev/fixture", async (FixtureRequest request, HttpContext http, IdentityDbContext identity, CustomersDbContext customers, CancellationToken ct) =>
        {
            if (!ValidKey(http.Request.Headers["X-Fixture-Key"].ToString(), app.Configuration["DevelopmentFixtures:Key"])) return Results.NotFound();
            var user = await identity.Users.SingleAsync(x => x.Id == request.UserId, ct);
            if (!user.EmailConfirmed || !user.Contact.EndsWith("@smoke.invalid", StringComparison.Ordinal)) return Results.BadRequest();
            if (request.Staff)
            {
                var name = "FIXTURE_" + request.UserId.ToString("N");
                var role = await identity.Roles.SingleOrDefaultAsync(x => x.NormalizedName == name, ct);
                if (role is null)
                {
                    role = new ApplicationRole { Id = Guid.NewGuid(), Name = name, NormalizedName = name, DisplayName = "Local smoke fixture" };
                    identity.Roles.Add(role);
                    foreach (var code in Permissions.All) identity.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = code });
                    identity.UserRoles.Add(new ApplicationUserRole { UserId = user.Id, RoleId = role.Id });
                    // Existing sessions become stale; smoke logs in again using the real OTP flow.
                    user.AuthorizationVersion++;
                    await identity.SaveChangesAsync(ct);
                }
            }
            var customer = await customers.Customers.SingleOrDefaultAsync(x => x.IdentityUserId == request.UserId, ct);
            if (customer is not null) { customer.Status = CustomerStatus.Active; customer.KycStatus = KycStatus.Verified; await customers.SaveChangesAsync(ct); }
            return Results.Ok(new { status = "prepared" });
        }).AllowAnonymous();
    }
    public static bool ValidKey(string actual, string? expected) => expected is { Length: >= 32 } &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    public static void AddDevelopmentOtp(this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        if (!config.GetValue<bool>("DevelopmentFixtures:Enabled")) return;
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing")) throw new InvalidOperationException("Development OTP is forbidden.");
        services.RemoveAll<IOtpSender>(); services.AddSingleton<IOtpSender, DevelopmentOtpSender>();
    }
}
public sealed record FixtureRequest(Guid UserId, bool Staff = false);
public sealed class DevelopmentOtpSender(IConfiguration config) : IOtpSender
{
    public async Task SendAsync(Guid challengeId, string target, string code, OtpPurpose purpose, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var message = new HttpRequestMessage(HttpMethod.Post, (config["DevelopmentFixtures:Sink"] ?? "http://notification-sink:8080") + "/otp")
        { Content = JsonContent.Create(new { challengeId, code }) };
        message.Headers.Add("X-Fixture-Key", config["DevelopmentFixtures:Key"]);
        using var response = await client.SendAsync(message, cancellationToken); response.EnsureSuccessStatusCode();
    }
}
