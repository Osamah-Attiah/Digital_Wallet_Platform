using System.Security.Claims;
using DigitalWallet.Beneficiaries.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalWallet.Beneficiaries;

public static class BeneficiariesEndpoints
{
    private const string Permission = "Permission:beneficiary.manage.own";

    public static IEndpointRouteBuilder MapBeneficiariesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/beneficiaries").RequireAuthorization(Permission);
        group.MapPost("", async (AddBeneficiaryRequest request, ClaimsPrincipal user, IBeneficiaryService service, CancellationToken ct) =>
            await Run(() => service.AddAsync(UserId(user), request, ct), created: true)).RequireRateLimiting("beneficiary-lookup");
        group.MapPost("/{id:guid}/verify", async (Guid id, VerifyBeneficiaryRequest request, ClaimsPrincipal user, IBeneficiaryService service, CancellationToken ct) =>
            await Run(() => service.VerifyAsync(UserId(user), id, request, ct))).RequireRateLimiting("beneficiary-otp");
        group.MapPost("/{id:guid}/otp", async (Guid id, ClaimsPrincipal user, IBeneficiaryService service, CancellationToken ct) =>
            await Run(() => service.ResendOtpAsync(UserId(user), id, ct))).RequireRateLimiting("beneficiary-otp");
        group.MapPost("/{id:guid}/activate", async (Guid id, ActivateBeneficiaryRequest request, ClaimsPrincipal user, IBeneficiaryService service, CancellationToken ct) =>
            await Run(() => service.ActivateAsync(UserId(user), id, request, ct)));
        group.MapGet("", async (string? cursor, int? pageSize, ClaimsPrincipal user, IBeneficiaryService service, CancellationToken ct) =>
            await Run(() => service.ListAsync(UserId(user), cursor, pageSize, ct)));
        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IBeneficiaryService service, CancellationToken ct) =>
            await Run(() => service.GetAsync(UserId(user), id, ct)));
        group.MapDelete("/{id:guid}", async (Guid id, Guid? concurrencyVersion, ClaimsPrincipal user, IBeneficiaryService service, CancellationToken ct) =>
        {
            try { await service.DeleteAsync(UserId(user), id, concurrencyVersion, ct); return Results.NoContent(); }
            catch (BeneficiaryRuleException ex) { return Problem(ex); }
        });
        return endpoints;
    }

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException());
    private static async Task<IResult> Run<T>(Func<Task<T>> action, bool created = false)
    {
        try { var response = await action(); return created ? Results.Created(string.Empty, response) : Results.Ok(response); }
        catch (BeneficiaryRuleException ex) { return Problem(ex); }
    }
    private static IResult Problem(BeneficiaryRuleException ex) => Results.Problem(statusCode: ex.StatusCode, title: ex.Message,
        type: $"https://digitalwallet.local/problems/{ex.Code.ToLowerInvariant().Replace('_', '-')}",
        extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
}
