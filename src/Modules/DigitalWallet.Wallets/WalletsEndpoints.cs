using System.Security.Claims;
using DigitalWallet.Wallets.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Wallets.Domain;
using Microsoft.Extensions.Hosting;
namespace DigitalWallet.Wallets;
public static class WalletsEndpoints
{
    private const string Permission = "Permission:";
    public static IEndpointRouteBuilder MapWalletsEndpoints(this IEndpointRouteBuilder endpoints, IHostEnvironment environment)
    {
        var wallets = endpoints.MapGroup("/api/v1/wallets");
        wallets.MapPost("", async (CreateWalletRequest request, ClaimsPrincipal user, IWalletService service, CancellationToken ct) => await Run(() => service.CreateAsync(UserId(user), request, ct), true)).RequireAuthorization(Permission + "wallet.create.own");
        wallets.MapGet("", async (ClaimsPrincipal user, IWalletService service, CancellationToken ct) => await Run(() => service.ListMineAsync(UserId(user), ct))).RequireAuthorization(Permission + "wallet.view.own");
        wallets.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IWalletService service, CancellationToken ct) => await Run(() => service.GetMineAsync(id, UserId(user), ct))).RequireAuthorization(Permission + "wallet.view.own");
        wallets.MapGet("/{id:guid}/balance", async (Guid id, ClaimsPrincipal user, IWalletService service, ILedgerReader ledger, CancellationToken ct) => await Run(async () =>
        {
            var wallet = await service.GetMineAsync(id, UserId(user), ct);
            return await ledger.GetWalletBalanceAsync(wallet.Id, wallet.Currency, ct)
                ?? throw new LedgerException("LEDGER_UNAVAILABLE", "Wallet ledger provisioning is pending.");
        })).RequireAuthorization(Permission + "wallet.view.own");
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            wallets.MapPost("/{id:guid}/demo-funds", async (Guid id, DemoFundsRequest request, ClaimsPrincipal user,
                IWalletService service, ILedgerAccounts accounts, ILedgerPosting posting, CancellationToken ct) => await Run(async () =>
            {
                var actor = UserId(user);
                var wallet = await service.GetMineAsync(id, actor, ct);
                if (wallet.Status != WalletStatus.Active) throw new WalletRuleException("WALLET_NOT_ACTIVE", 422, "An active wallet is required.");
                var account = await accounts.EnsureWalletAccountAsync(id, wallet.Currency, ct);
                var control = await accounts.EnsureSystemAccountAsync(SystemAccount.AdjustmentControl, wallet.Currency, ct);
                return await posting.PostAsync(new PostJournal("DemoFunding", request.SourceId, wallet.Currency,
                    [new(control, PostingSide.Debit, request.AmountMinor), new(account, PostingSide.Credit, request.AmountMinor)], request.Reason, actor), ct);
            })).RequireAuthorization(Permission + "wallet.create.own");
        }
        var admin = endpoints.MapGroup("/api/v1/admin/wallets");
        admin.MapPost("/{id:guid}/freeze", async (Guid id, WalletStateChangeRequest request, ClaimsPrincipal user, IWalletService service, CancellationToken ct) => await Run(() => service.FreezeAsync(id, request, UserId(user), ct))).RequireAuthorization(Permission + "wallet.freeze");
        admin.MapPost("/{id:guid}/unfreeze", async (Guid id, WalletStateChangeRequest request, ClaimsPrincipal user, IWalletService service, CancellationToken ct) => await Run(() => service.UnfreezeAsync(id, request, UserId(user), ct))).RequireAuthorization(Permission + "wallet.unfreeze");
        return endpoints;
    }
    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException());
    private static async Task<IResult> Run<T>(Func<Task<T>> action, bool created = false)
    {
        try { var item = await action(); return created ? Results.Created(string.Empty, item) : Results.Ok(item); }
        catch (WalletRuleException ex) { return Problem(ex.Code, ex.Message, ex.StatusCode); }
        catch (LedgerException ex)
        {
            var status = ex.Code switch
            {
                "IDEMPOTENCY_KEY_REUSED" or "ALREADY_REVERSED" => 409,
                "INSUFFICIENT_BALANCE" or "AMOUNT_OUT_OF_RANGE" => 422,
                "LEDGER_UNAVAILABLE" => 503,
                "JOURNAL_NOT_FOUND" => 404,
                _ => 400
            };
            return Problem(ex.Code, ex.Message, status);
        }
    }
    private static IResult Problem(string code, string message, int status) => Results.Problem(statusCode: status,
        title: message, type: $"https://digitalwallet.local/problems/{code.ToLowerInvariant().Replace('_', '-')}",
        extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record DemoFundsRequest(long AmountMinor, string SourceId, string Reason);
