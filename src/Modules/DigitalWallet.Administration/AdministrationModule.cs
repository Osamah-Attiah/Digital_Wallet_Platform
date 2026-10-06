using System.Security.Claims;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Wallets.Contracts;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Transactions.Contracts;
using DigitalWallet.Transfers.Contracts;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.SharedKernel.Administration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace DigitalWallet.Administration;

public sealed class AdminSetting
{
    public string Key { get; set; } = ""; public string Value { get; set; } = ""; public Guid ConcurrencyVersion { get; set; } = Guid.NewGuid();
}
public sealed record SettingChange(string Value, Guid ConcurrencyVersion, string Reason);
public sealed class AdministrationDbContext(DbContextOptions<AdministrationDbContext> o) : DbContext(o)
{
    public const string Schema = "administration"; public DbSet<AdminSetting> Settings => Set<AdminSetting>();
    protected override void OnModelCreating(ModelBuilder b)
    { b.HasDefaultSchema(Schema); b.Entity<AdminSetting>(e => { e.ToTable("settings"); e.HasKey(x => x.Key); e.Property(x => x.Key).HasMaxLength(80); e.Property(x => x.Value).HasMaxLength(160); e.Property(x => x.ConcurrencyVersion).IsConcurrencyToken(); }); }
}
public static class AdministrationModule
{
    public static IServiceCollection AddAdministrationModule(this IServiceCollection s, IConfiguration c)
    { s.AddScoped<AdministrativeReads>(); s.AddDbContext<AdministrationDbContext>(o => o.UseNpgsql(c["Platform:PostgreSql:ConnectionString"], n => n.MigrationsHistoryTable("__EFMigrationsHistory", AdministrationDbContext.Schema))); return s; }
    public static async Task InitializeAdministrationModuleAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AdministrationDbContext>(); await db.Database.MigrateAsync(ct);
        if (!await db.Settings.AnyAsync(ct)) { db.Settings.AddRange(new AdminSetting { Key = "support.locale", Value = "ar-SA" }, new AdminSetting { Key = "dashboard.refreshSeconds", Value = "30" }); await db.SaveChangesAsync(ct); }
    }
    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var a = endpoints.MapGroup("/api/v1/admin").RequireAuthorization();
        a.MapGet("/session", async (ClaimsPrincipal u, IStaffAccess access, CancellationToken ct) =>
        {
            var id = Actor(u); var permissions = await access.PermissionsAsync(id, ct);
            if (!permissions.Any(IsStaffPermission)) return Results.Forbid();
            return Results.Ok(new StaffSession(id, permissions, await access.RecentMfaAsync(id, Session(u), ct)));
        });
        a.MapPost("/session/mfa", async (ClaimsPrincipal u, IStaffAccess access, CancellationToken ct) =>
        {
            if (!(await access.PermissionsAsync(Actor(u), ct)).Any(IsStaffPermission)) return Results.Forbid();
            return Results.Ok(await access.IssueMfaAsync(Actor(u), Session(u), ct));
        }).RequireRateLimiting("reversal-otp");
        a.MapPost("/session/mfa/verify", async (MfaVerification r, ClaimsPrincipal u, IStaffAccess access, CancellationToken ct) =>
        {
            if (!(await access.PermissionsAsync(Actor(u), ct)).Any(IsStaffPermission)) return Results.Forbid();
            return await access.VerifyMfaAsync(Actor(u), Session(u), r.ChallengeId, r.Code, ct) ? Results.Ok(new { recentMfa = true }) : Results.Problem(statusCode: 400, title: "Verification failed.", extensions: new Dictionary<string, object?> { ["code"] = "OTP_INVALID" });
        }).RequireRateLimiting("reversal-otp");
        a.MapGet("/dashboard", async (AdministrativeReads reads, CancellationToken ct) => Results.Ok(await reads.DashboardAsync(ct))).RequireAuthorization("Permission:operations.view").RequireAuthorization("Permission:customer.view").RequireAuthorization("Permission:transaction.view.any");
        a.MapGet("/operations/messaging", async (IMessagingMonitor monitor, CancellationToken ct) => Results.Ok(await monitor.GetAsync(ct))).RequireAuthorization("Permission:transaction.view.any");
        a.MapGet("/customers/search", ([AsParameters] CustomerSearch query, ClaimsPrincipal u, IAdminCustomerReader customers, CancellationToken ct) => Run(() => customers.SearchAsync(query, Actor(u), ct))).RequireAuthorization("Permission:customer.view");
        a.MapPost("/customers/{id:guid}/pii", async (Guid id, PiiReveal r, IAdminCustomerReader customers, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 500) return Results.BadRequest();
            var c = await customers.GetAsync(id, true, ct); return c == null ? Results.NotFound() : Results.Ok(c);
        }).RequireAuthorization("Permission:customer.view").RequireAuthorization("Permission:customer.pii.view");
        a.MapGet("/customers/{id:guid}/wallets", async (Guid id, IAdminWalletReader wallets, CancellationToken ct) => Results.Ok(await wallets.ForCustomerAsync(id, ct))).RequireAuthorization("Permission:wallet.view.any");
        a.MapGet("/wallets/{id:guid}", async (Guid id, IAdminWalletReader wallets, ILedgerReader ledger, CancellationToken ct) =>
        {
            var w = await wallets.GetAsync(id, ct); return w == null ? Results.NotFound() : Results.Ok(new { wallet = w, balance = await ledger.GetWalletBalanceAsync(id, w.Currency, ct) });
        }).RequireAuthorization("Permission:wallet.view.any");
        a.MapGet("/transfers", (string? cursor, int? pageSize, ClaimsPrincipal u, ITransferReader transfers, CancellationToken ct) => Run(() => transfers.ListAsync(Actor(u), true, cursor, pageSize, ct))).RequireAuthorization("Permission:transfer.view.any");
        a.MapGet("/transfers/{id:guid}", (Guid id, ClaimsPrincipal u, ITransferReader transfers, CancellationToken ct) => Run(() => transfers.GetAsync(Actor(u), id, true, ct))).RequireAuthorization("Permission:transfer.view.any");
        a.MapGet("/reversal-requests", (string? status, string? cursor, int? pageSize, ClaimsPrincipal u, IAdminTransactionReader t, CancellationToken ct) => Run(() => t.ReversalsAsync(status, cursor, pageSize, Actor(u), ct))).RequireAuthorization("Permission:transaction.view.any");
        a.MapGet("/roles", async (IStaffAccess access, CancellationToken ct) => Results.Ok(await access.RolesAsync(ct))).RequireAuthorization("Permission:role.manage");
        a.MapGet("/permissions", async (IStaffAccess access, CancellationToken ct) => Results.Ok(await access.CatalogAsync(ct))).RequireAuthorization("Permission:permission.assign");
        a.MapPost("/roles", (RoleCreate input, ClaimsPrincipal u, IStaffAccess access, CancellationToken ct) => Run(() => access.CreateRoleAsync(Actor(u), input, ct))).RequireAuthorization("Permission:role.manage").RequireAuthorization("Permission:permission.assign");
        a.MapPut("/roles/{id:guid}", (Guid id, RoleChange change, ClaimsPrincipal u, IStaffAccess access, CancellationToken ct) => Run(() => access.ChangeRoleAsync(Actor(u), id, change, ct))).RequireAuthorization("Permission:role.manage").RequireAuthorization("Permission:permission.assign");
        a.MapPut("/staff/{id:guid}/roles", (Guid id, StaffRoleChange change, ClaimsPrincipal u, IStaffAccess access, CancellationToken ct) => Run(() => access.AssignAsync(Actor(u), id, change, ct))).RequireAuthorization("Permission:staff.manage").RequireAuthorization("Permission:permission.assign");
        a.MapGet("/settings", async (AdministrativeReads reads, CancellationToken ct) => Results.Ok(await reads.SettingsAsync(ct))).RequireAuthorization("Permission:configuration.view");
        a.MapPut("/settings/{key}", (string key, SettingChange change, AdministrationDbContext db, CancellationToken ct) => Run(async () =>
        {
            if (key is not ("support.locale" or "dashboard.refreshSeconds")) throw new AdminException("SETTING_NOT_FOUND", 404);
            if (string.IsNullOrWhiteSpace(change.Reason) || change.Reason.Length > 500 || change.ConcurrencyVersion == Guid.Empty ||
                key == "support.locale" && change.Value is not ("ar-SA" or "en-US") || key == "dashboard.refreshSeconds" && (!int.TryParse(change.Value, out var seconds) || seconds < 15 || seconds > 300)) throw new AdminException("VALIDATION_ERROR");
            var setting = await db.Settings.SingleOrDefaultAsync(x => x.Key == key, ct) ?? throw new AdminException("SETTING_NOT_FOUND", 404);
            db.Entry(setting).Property(x => x.ConcurrencyVersion).OriginalValue = change.ConcurrencyVersion; setting.Value = change.Value; setting.ConcurrencyVersion = Guid.NewGuid();
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { throw new AdminException("CONCURRENCY_CONFLICT", 409); }
            return setting;
        })).RequireAuthorization("Permission:configuration.manage");
        return endpoints;
    }
    public static bool IsStaffPermission(string code) => code is "customer.view" or "wallet.view.any" or "transaction.view.any" or "transfer.view.any" or "report.view" or "report.generate" or "audit.view" or "role.manage" or "permission.assign" or "staff.manage" or "configuration.view" or "configuration.manage" or "operations.view" or "wallet.freeze" or "wallet.unfreeze" or "kyc.review" or "customer.block" or "customer.suspend" or "customer.unblock" or "transaction.reverse.request" or "transaction.reverse.approve";
    public static Guid Actor(ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue("sub")!);
    public static Guid Session(ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue("sid")!);
    public static async Task<IResult> Run<T>(Func<Task<T>> work)
    { try { return Results.Ok(await work()); } catch (AdminException ex) { return Results.Problem(statusCode: ex.Status, title: ex.Message, extensions: new Dictionary<string, object?> { ["code"] = ex.Code }); } }
}
public sealed record MfaVerification(Guid ChallengeId, string Code);
public sealed record PiiReveal(string Reason);
public sealed class AdministrationDbContextFactory : IDesignTimeDbContextFactory<AdministrationDbContext>
{
    public AdministrationDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AdministrationDbContext>().UseNpgsql("Host=localhost;Database=design_only", n => n.MigrationsHistoryTable("__EFMigrationsHistory", AdministrationDbContext.Schema)).Options);
}
