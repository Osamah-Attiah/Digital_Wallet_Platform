using System.Security.Claims;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Transactions.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalWallet.Transactions.Endpoints;
public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/admin/transactions", ([AsParameters] HistoryQuery query, ClaimsPrincipal user, TransactionService service, CancellationToken ct) =>
            Run(() => service.ListAsync(UserId(user), true, query, ct))).RequireAuthorization("Permission:transaction.view.any");
        var history = endpoints.MapGroup("/api/v1/transactions").RequireAuthorization();
        history.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IAuthorizationService auth, TransactionService service, CancellationToken ct) =>
        {
            var any = (await auth.AuthorizeAsync(user, "Permission:transaction.view.any")).Succeeded;
            if (!any && !(await auth.AuthorizeAsync(user, "Permission:transaction.view.own")).Succeeded) return Results.Forbid();
            return await Run(() => service.GetAsync(UserId(user), any, id, ct));
        });
        history.MapGet("", async ([AsParameters] HistoryQuery query, ClaimsPrincipal user, IAuthorizationService auth, TransactionService service, CancellationToken ct) =>
        {
            var any = (await auth.AuthorizeAsync(user, "Permission:transaction.view.any")).Succeeded;
            if (!any && !(await auth.AuthorizeAsync(user, "Permission:transaction.view.own")).Succeeded) return Results.Forbid();
            return await Run(() => service.ListAsync(UserId(user), any, query, ct));
        });
        var admin = endpoints.MapGroup("/api/v1/admin").RequireAuthorization();
        admin.MapPost("/transfers/{id:guid}/reversal-requests", (Guid id, RequestReversal request, HttpContext http, TransactionService service, CancellationToken ct) =>
            Run(() => service.RequestAsync(UserId(http.User), id, http.Request.Headers["Idempotency-Key"].ToString(), request, ct), true))
            .RequireAuthorization("Permission:transaction.reverse.request");
        admin.MapGet("/reversal-requests/{id:guid}", async (Guid id, ClaimsPrincipal user, IAuthorizationService auth, TransactionService service, CancellationToken ct) =>
        {
            if (!(await auth.AuthorizeAsync(user, "Permission:transaction.reverse.request")).Succeeded &&
                !(await auth.AuthorizeAsync(user, "Permission:transaction.reverse.approve")).Succeeded &&
                !(await auth.AuthorizeAsync(user, "Permission:transaction.view.any")).Succeeded) return Results.Forbid();
            return await Run(() => service.GetRequestAsync(id, ct));
        });
        admin.MapPost("/reversal-requests/{id:guid}/otp", (Guid id, ClaimsPrincipal user, TransactionService service, CancellationToken ct) =>
            Run(() => service.IssueOtpAsync(UserId(user), id, ct))).RequireAuthorization("Permission:transaction.reverse.approve").RequireRateLimiting("reversal-otp");
        admin.MapPost("/reversal-requests/{id:guid}/approve", (Guid id, ApproveReversal request, HttpContext http, TransactionService service, CancellationToken ct) =>
            Run(() => service.ApproveAsync(UserId(http.User), id, http.Request.Headers["Idempotency-Key"].ToString(), request, ct)))
            .RequireAuthorization("Permission:transaction.reverse.approve").RequireRateLimiting("reversal-approve");
        admin.MapPost("/reversal-requests/{id:guid}/reject", (Guid id, RejectReversal request, ClaimsPrincipal user, TransactionService service, CancellationToken ct) =>
            Run(() => service.RejectAsync(UserId(user), id, request, ct))).RequireAuthorization("Permission:transaction.reverse.approve");
        return endpoints;
    }
    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
    private static async Task<IResult> Run<T>(Func<Task<T>> action, bool created = false)
    {
        try { var value = await action(); return created ? Results.Created(string.Empty, value) : Results.Ok(value); }
        catch (TransactionsException ex) { return Problem(ex.Code); }
        catch (LedgerException ex) { return Problem(ex.Code); }
    }
    private static IResult Problem(string code) => Results.Problem(statusCode: code switch
    {
        "TRANSACTION_NOT_FOUND" or "REVERSAL_NOT_FOUND" or "JOURNAL_NOT_FOUND" => 404,
        "SELF_APPROVAL_FORBIDDEN" => 403,
        "IDEMPOTENCY_KEY_REUSED" or "ALREADY_REVERSED" or "REVERSAL_PENDING" or "STATE_CONFLICT" or "CONCURRENCY_CONFLICT" => 409,
        "NOT_REVERSIBLE" or "INSUFFICIENT_RECOVERY_FUNDS" or "AMOUNT_OUT_OF_RANGE" => 422,
        "LEDGER_UNAVAILABLE" => 503,
        _ => 400
    }, title: "The transaction request could not be completed.",
        type: $"https://digitalwallet.local/problems/{code.ToLowerInvariant().Replace('_', '-')}",
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
