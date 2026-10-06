using System.Security.Claims;
using DigitalWallet.Messaging.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalWallet.Notifications;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<NotificationsDbContext>(x => x.UseNpgsql(config["Platform:PostgreSql:ConnectionString"],
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", NotificationsDbContext.Schema)));
        services.AddScoped<NotificationService>(); services.AddScoped<IEventConsumer, NotificationConsumer>();
        services.TryAddSingleton<INotificationCheckpoint, NoopNotificationCheckpoint>();
        services.AddSingleton<IExternalNotificationChannel, ExternalNotificationStub>();
        return services;
    }
    public static async Task InitializeNotificationsModuleAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync(ct);
    }
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/notifications").RequireAuthorization();
        group.MapGet("", (ClaimsPrincipal user, string? cursor, int? pageSize, NotificationService s, CancellationToken ct) => Run(() => s.ListAsync(UserId(user), cursor, pageSize, ct)));
        group.MapGet("/{id:guid}", (ClaimsPrincipal user, Guid id, NotificationService s, CancellationToken ct) => Run(() => s.GetAsync(UserId(user), id, ct)));
        group.MapPost("/{id:guid}/read", (ClaimsPrincipal user, Guid id, NotificationService s, CancellationToken ct) => Run(() => s.MarkReadAsync(UserId(user), id, ct)));
        group.MapGet("/preferences", (ClaimsPrincipal user, NotificationService s, CancellationToken ct) => Run(() => s.PreferencesAsync(UserId(user), ct)));
        group.MapPut("/preferences", (ClaimsPrincipal user, PreferenceRequest p, NotificationService s, CancellationToken ct) => Run(() => s.SetPreferencesAsync(UserId(user), p, ct)));
        return endpoints;
    }
    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (NotificationException ex)
        {
            return Results.Problem(statusCode: ex.Code == "NOTIFICATION_NOT_FOUND" ? 404 : 400,
                title: "The notification request could not be completed.",
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }
}
