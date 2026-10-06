using DigitalWallet.Ledger.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalWallet.Ledger.Endpoints;

public static class LedgerEndpoints
{
    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/admin/ledger/reconciliation", async (ILedgerReconciliation service, CancellationToken ct) =>
            Results.Ok(await service.RunAsync(ct))).RequireAuthorization("Permission:audit.view");
        return endpoints;
    }
}
