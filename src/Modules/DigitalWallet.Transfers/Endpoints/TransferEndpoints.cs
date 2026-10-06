using System.Security.Claims;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.Transfers.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalWallet.Transfers.Endpoints;
public static class TransferEndpoints
{
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/transfers").RequireAuthorization();
        group.MapPost("/quotes", (QuoteRequest request, ClaimsPrincipal user, TransferService service, CancellationToken ct) =>
            Run(() => service.QuoteAsync(UserId(user), request, ct))).RequireAuthorization("Permission:transfer.create");
        group.MapPost("", (CreateTransferRequest request, HttpContext http, TransferService service, CancellationToken ct) =>
            Run(() => service.CreateAsync(UserId(http.User), http.Request.Headers["Idempotency-Key"].ToString(), request, ct), true)).RequireAuthorization("Permission:transfer.create");
        group.MapPost("/{id:guid}/confirm", (Guid id, ConfirmTransferRequest request, ClaimsPrincipal user, TransferService service, CancellationToken ct) =>
            Run(() => service.ConfirmAsync(UserId(user), id, request, ct))).RequireAuthorization("Permission:transfer.create").RequireRateLimiting("transfer-confirm");
        group.MapPost("/{id:guid}/otp", (Guid id, ClaimsPrincipal user, TransferService service, CancellationToken ct) =>
            Run(() => service.ResendAsync(UserId(user), id, ct))).RequireAuthorization("Permission:transfer.create").RequireRateLimiting("transfer-otp");
        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IAuthorizationService auth, TransferService service, CancellationToken ct) =>
        {
            var any = (await auth.AuthorizeAsync(user, "Permission:transfer.view.any")).Succeeded;
            if (!any && !(await auth.AuthorizeAsync(user, "Permission:transfer.view.own")).Succeeded) return Results.Forbid();
            return await Run(() => service.GetAsync(UserId(user), id, any, ct));
        });
        group.MapGet("", async (string? cursor, int? pageSize, ClaimsPrincipal user, IAuthorizationService auth, TransferService service, CancellationToken ct) =>
        {
            var any = (await auth.AuthorizeAsync(user, "Permission:transfer.view.any")).Succeeded;
            if (!any && !(await auth.AuthorizeAsync(user, "Permission:transfer.view.own")).Succeeded) return Results.Forbid();
            return await Run(() => service.ListAsync(UserId(user), any, cursor, pageSize, ct));
        });
        return endpoints;
    }
    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
    private static async Task<IResult> Run<T>(Func<Task<T>> action, bool created = false)
    {
        try { var result = await action(); return created ? Results.Created(string.Empty, result) : Results.Ok(result); }
        catch (TransferException ex) { return Problem(ex.Code); }
        catch (LedgerException ex) { return Problem(ex.Code); }
        catch (RiskLimitsException ex) { return Problem(ex.Code); }
    }
    private static IResult Problem(string code)
    {
        var status = code switch
        {
            "QUOTE_NOT_FOUND" or "TRANSFER_NOT_FOUND" or "BENEFICIARY_NOT_FOUND" or "WALLET_NOT_FOUND" or "CUSTOMER_NOT_FOUND" => 404,
            "QUOTE_EXPIRED" or "TRANSFER_EXPIRED" => 410,
            "QUOTE_USED" or "QUOTE_STALE" or "IDEMPOTENCY_KEY_REUSED" or "STATE_CONFLICT" or "CONCURRENCY_CONFLICT" => 409,
            "IDEMPOTENCY_KEY_REQUIRED" or "VALIDATION_ERROR" or "OTP_INVALID" or "OTP_EXPIRED" or "OTP_ATTEMPTS_EXCEEDED" => 400,
            "LEDGER_UNAVAILABLE" or "RULES_NOT_CONFIGURED" => 503,
            _ => 422
        };
        return Results.Problem(statusCode: status, title: "The transfer request could not be completed.",
            type: $"https://digitalwallet.local/problems/{code.ToLowerInvariant().Replace('_', '-')}", extensions: new Dictionary<string, object?> { ["code"] = code });
    }
}
