using System.Security.Claims;
using DigitalWallet.RiskLimits.Application;
using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.RiskLimits.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalWallet.RiskLimits.Endpoints;

public static class RiskLimitsEndpoints
{
    public static IEndpointRouteBuilder MapRiskLimitsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/risk-limits/rules").RequireAuthorization();
        group.MapPost("", (CreateRule request, ClaimsPrincipal user, IRuleAdministration service, CancellationToken ct) =>
            Run(() => service.CreateAsync(request, Actor(user), ct), true)).RequireAuthorization("Permission:risk.rules.create");
        group.MapPost("/{id:guid}/publish", (Guid id, ClaimsPrincipal user, IRuleAdministration service, CancellationToken ct) =>
            Run(() => service.PublishAsync(id, Actor(user), ct))).RequireAuthorization("Permission:risk.rules.publish");
        group.MapGet("/{id:guid}", (Guid id, IRuleAdministration service, CancellationToken ct) =>
            Run(() => service.GetAsync(id, ct))).RequireAuthorization("Permission:risk.rules.read");
        group.MapGet("", (RuleKind kind, RuleScope scope, string key, string currency, string transferType,
            int? afterVersion, int? pageSize, IRuleAdministration service, CancellationToken ct) =>
            Run(() => service.ListAsync(kind, scope, key, currency, transferType, afterVersion ?? 0, pageSize ?? 20, ct)))
            .RequireAuthorization("Permission:risk.rules.read");
        return endpoints;
    }
    private static Guid Actor(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
    private static async Task<IResult> Run<T>(Func<Task<T>> action, bool created = false)
    {
        try { var value = await action(); return created && value is Rule r ? Results.Created($"/api/v1/admin/risk-limits/rules/{r.Id}", value) : Results.Ok(value); }
        catch (RiskLimitsException ex)
        {
            var status = ex.Code switch { "VALIDATION_ERROR" => 400, "RULE_NOT_FOUND" => 404, _ => 409 };
            return Results.Problem(statusCode: status, title: ex.Message,
                type: $"https://digitalwallet.local/problems/{ex.Code.ToLowerInvariant().Replace('_', '-')}",
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }
}
