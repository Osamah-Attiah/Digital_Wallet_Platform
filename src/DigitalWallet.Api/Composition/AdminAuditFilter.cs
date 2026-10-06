using System.Security.Claims;
using DigitalWallet.Administration;
using DigitalWallet.Audit;
using DigitalWallet.Audit.Contracts;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Wallets.Persistence;
using DigitalWallet.SharedKernel.Correlation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace DigitalWallet.Api.Composition;

public sealed class AdminAuditFilter(IConfiguration config, TimeProvider clock, ICorrelationContext correlation, IStaffAccess access) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var h = context.HttpContext; var path = h.Request.Path.Value ?? ""; if (!path.StartsWith("/api/v1/admin/", StringComparison.Ordinal)) return await next(context);
        h.Response.Headers.CacheControl = "no-store";
        using var span = DigitalWallet.SharedKernel.Observability.WalletTelemetry.Start(DigitalWallet.SharedKernel.Observability.TelemetryModule.Administration, DigitalWallet.SharedKernel.Observability.OperationName.Admin);
        var actor = Guid.Parse(h.User.FindFirstValue("sub")!);
        var sensitive = path.EndsWith("/freeze", StringComparison.Ordinal) || path.EndsWith("/unfreeze", StringComparison.Ordinal) || h.Request.Method == "PUT" || path == "/api/v1/admin/roles" && h.Request.Method == "POST";
        if (sensitive && !await access.RecentMfaAsync(actor, Guid.Parse(h.User.FindFirstValue("sid")!), h.RequestAborted))
        {
            await StandaloneAsync(h, "Denied", actor); return Results.Problem(statusCode: 403, title: "Recent additional verification is required.", extensions: new Dictionary<string, object?> { ["code"] = "RECENT_MFA_REQUIRED" });
        }
        var atomic = sensitive || h.Request.Method == "POST" && path.Contains("/customers/", StringComparison.Ordinal) && !path.EndsWith("/pii", StringComparison.Ordinal);
        if (!atomic)
        {
            try
            {
                var value = await next(context); var status = Status(value);
                // Report creation, download and reversal success are audited inside their owning transaction.
                if (status >= 400 || path.EndsWith("/pii", StringComparison.Ordinal) || path.Contains("/audit-logs", StringComparison.Ordinal)) await StandaloneAsync(h, status == 403 ? "Denied" : status >= 400 ? "Failed" : "Success", actor);
                return value;
            }
            catch { await StandaloneAsync(h, "Failed", actor); throw; }
        }
        await using var connection = new NpgsqlConnection(config["Platform:PostgreSql:ConnectionString"]); await connection.OpenAsync(h.RequestAborted);
        await using var tx = await connection.BeginTransactionAsync(h.RequestAborted);
        // Composition is the only place allowed to enlist multiple owner contexts. Each still queries its own tables.
        var owners = new DbContext[] { h.RequestServices.GetRequiredService<CustomersDbContext>(), h.RequestServices.GetRequiredService<WalletsDbContext>(), h.RequestServices.GetRequiredService<IdentityDbContext>(), h.RequestServices.GetRequiredService<AdministrationDbContext>() };
        foreach (var owner in owners) { owner.Database.SetDbConnection(connection, contextOwnsConnection: false); await owner.Database.UseTransactionAsync(tx, h.RequestAborted); }
        try
        {
            var value = await next(context); var status = Status(value);
            if (status >= 400) { await tx.RollbackAsync(CancellationToken.None); await StandaloneAsync(h, status == 403 ? "Denied" : "Failed", actor); return value; }
            await using var db = await AuditWriter.EnlistAsync(connection, tx, h.RequestAborted);
            await new AuditWriter(db, clock).AppendAsync(Fact(h, actor, "Success", correlation.Id), h.RequestAborted);
            await tx.CommitAsync(h.RequestAborted); h.Items["AdminAuditRecorded"] = true; return value;
        }
        catch { await tx.RollbackAsync(CancellationToken.None); await StandaloneAsync(h, "Failed", actor); throw; }
    }
    private static int Status(object? value) => value is IStatusCodeHttpResult r ? r.StatusCode ?? 200 : 200;
    private async Task StandaloneAsync(HttpContext h, string result, Guid actor)
    {
        await using var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(config["Platform:PostgreSql:ConnectionString"]).Options);
        await new AuditWriter(db, clock).AppendAsync(Fact(h, actor, result, correlation.Id), CancellationToken.None); h.Items["AdminAuditRecorded"] = true;
    }
    public static AuditFact Fact(HttpContext h, Guid actor, string result, string correlation)
    {
        var segments = (h.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resource = segments.Length > 3 && new[] { "customers", "wallets", "transfers", "transactions", "reversal-requests", "reports", "roles", "staff", "settings", "audit-logs", "session", "dashboard", "operations" }.Contains(segments[3]) ? segments[3] : "Administration"; var id = segments.FirstOrDefault(x => Guid.TryParse(x, out _)) ?? "collection";
        var action = resource switch
        {
            "customers" => h.Request.Path.Value!.EndsWith("/pii", StringComparison.Ordinal) ? "customer.pii.view" : h.Request.Path.Value!.Contains("/kyc/", StringComparison.Ordinal) ? "customer.kyc.change" : "customer.status.change",
            "wallets" => h.Request.Path.Value!.EndsWith("/unfreeze", StringComparison.Ordinal) ? "wallet.unfreeze" : "wallet.freeze",
            "roles" or "staff" => "role.change",
            "settings" => "configuration.change",
            "audit-logs" => "audit.search",
            "reports" => "report.access",
            "transfers" or "reversal-requests" => "reversal.access",
            _ => "admin.access"
        };
        return new(actor, actor == Guid.Empty ? "Anonymous" : "Staff", action, resource, id, result, correlation, "AdministrativeReview");
    }
}
public sealed class AdminDeniedAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext h, IAuditWriter writer, ICorrelationContext correlation)
    {
        await next(h);
        if (h.Request.Path.StartsWithSegments("/api/v1/admin") && h.Response.StatusCode is 401 or 403 && !h.Items.ContainsKey("AdminAuditRecorded"))
        {
            Guid.TryParse(h.User.FindFirstValue("sub"), out var actor);
            await writer.AppendAsync(AdminAuditFilter.Fact(h, actor, "Denied", correlation.Id), CancellationToken.None);
        }
    }
}
