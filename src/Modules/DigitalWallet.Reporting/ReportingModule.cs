using System.Security.Claims;
using DigitalWallet.Reporting.Contracts;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.SharedKernel.Administration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace DigitalWallet.Reporting;

public sealed class ReportOptions
{
    public int MaxRows { get; init; } = 10000; public int MaxDays { get; init; } = 31; public int RetentionHours { get; init; } = 24; public int LeaseSeconds { get; init; } = 120;
    public string StoragePath { get; init; } = ".local/report-files"; public bool WorkersEnabled { get; init; } = true;
}
public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection s, IConfiguration c)
    {
        var options = c.GetSection("Reporting").Get<ReportOptions>() ?? new();
        if (options.MaxRows < 1 || options.MaxRows > 10000 || options.MaxDays < 1 || options.MaxDays > 31 || options.RetentionHours < 1 || options.RetentionHours > 168 || options.LeaseSeconds < 30 || options.LeaseSeconds > 600) throw new InvalidOperationException("Invalid reporting limits.");
        s.AddSingleton(options); s.AddDbContext<ReportingDbContext>(o => o.UseNpgsql(c["Platform:PostgreSql:ConnectionString"], n => n.MigrationsHistoryTable("__EFMigrationsHistory", ReportingDbContext.Schema)));
        s.TryAddSingleton<IReportFileStore, LocalReportFileStore>(); s.TryAddSingleton<IReportCheckpoint, NoopReportCheckpoint>();
        s.AddScoped<ReportService>(); s.AddScoped<ReportConsumer>(); s.AddScoped<IEventConsumer>(p => p.GetRequiredService<ReportConsumer>()); if (c["Hosting:Role"] is null or "Combined" or "Worker") s.AddHostedService<ReportRecoveryWorker>(); return s;
    }
    public static async Task InitializeReportingModuleAsync(this IServiceProvider services, CancellationToken ct = default)
    { await using var scope = services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<ReportingDbContext>().Database.MigrateAsync(ct); }
    public static IEndpointRouteBuilder MapReportingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var a = endpoints.MapGroup("/api/v1/admin/reports").RequireAuthorization();
        a.MapPost("", (ReportRequest request, HttpContext http, ReportService service, CancellationToken ct) => Run(() => service.CreateAsync(Actor(http.User), http.Request.Headers["Idempotency-Key"].ToString(), request, ct), true)).RequireAuthorization("Permission:report.generate");
        a.MapGet("", (string? cursor, int? pageSize, ClaimsPrincipal u, ReportService service, CancellationToken ct) => Run(() => service.ListAsync(Actor(u), cursor, pageSize, ct))).RequireAuthorization("Permission:report.view");
        a.MapGet("/{id:guid}", (Guid id, ClaimsPrincipal u, ReportService service, CancellationToken ct) => Run(() => service.GetAsync(Actor(u), id, ct))).RequireAuthorization("Permission:report.view");
        a.MapGet("/{id:guid}/download", async (Guid id, ClaimsPrincipal u, ReportService service, CancellationToken ct) =>
        {
            try { return Results.File(await service.DownloadAsync(Actor(u), id, ct), "text/csv; charset=utf-8", $"report-{id:N}-v1.csv"); }
            catch (AdminException ex) { return Problem(ex); }
        }).RequireAuthorization("Permission:report.view"); return endpoints;
    }
    private static Guid Actor(ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue("sub")!);
    private static async Task<IResult> Run<T>(Func<Task<T>> work, bool created = false)
    { try { var value = await work(); return created ? Results.Accepted(value: value) : Results.Ok(value); } catch (AdminException ex) { return Problem(ex); } }
    private static IResult Problem(AdminException ex) => Results.Problem(statusCode: ex.Status, title: ex.Message, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
}
public sealed class ReportRecoveryWorker(IServiceScopeFactory scopes, ReportOptions options, TimeProvider clock, ILogger<ReportRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.WorkersEnabled) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
                var now = clock.GetUtcNow(); var jobs = await db.Jobs.AsNoTracking().Where(x => x.Status == "Running" && x.LeaseUntil < now).OrderBy(x => x.LeaseUntil).Take(10).ToListAsync(ct);
                foreach (var j in jobs) await scope.ServiceProvider.GetRequiredService<ReportConsumer>().ConsumeAsync(WalletEvents.Report(j.Id, j.CreatedAt, j.CorrelationId), ct);
                var expired = await db.Jobs.AsNoTracking().Where(x => x.ExpiresAt <= now && x.Status != "Expired").Take(50).ToListAsync(ct);
                foreach (var j in expired) { await db.Jobs.Where(x => x.Id == j.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Expired").SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), ct); await scope.ServiceProvider.GetRequiredService<IReportFileStore>().DeleteAsync(j.Id, ct); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { logger.LogWarning(new EventId(5001, "ReportRecoveryFailed"), "{Module} {Outcome} {ErrorCode} {ActorType}", "Reporting", "Failed", "DEPENDENCY_UNAVAILABLE", "Worker"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
