using System.Security.Claims;
using DigitalWallet.Customers.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalWallet.Customers;

public static class CustomersEndpoints
{
    private const string Permission = "Permission:";
    public static IEndpointRouteBuilder MapCustomersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var me = endpoints.MapGroup("/api/v1/me");
        me.MapPost("/profile", async (CreateProfileRequest request, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.CreateAsync(UserId(user), request, ct), created: true)).RequireAuthorization(Permission + "profile.edit.own");
        me.MapGet("", async (ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.GetMineAsync(UserId(user), ct))).RequireAuthorization(Permission + "profile.view.own");
        me.MapPatch("", async (UpdateProfileRequest request, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.UpdateMineAsync(UserId(user), request, ct))).RequireAuthorization(Permission + "profile.edit.own");
        me.MapPost("/kyc/submit", async (StateChangeRequest request, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.SubmitKycAsync(UserId(user), request, ct))).RequireAuthorization(Permission + "profile.edit.own");
        me.MapGet("/kyc", async (ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.GetKycAsync(UserId(user), ct))).RequireAuthorization(Permission + "profile.view.own");

        var admin = endpoints.MapGroup("/api/v1/admin/customers");
        admin.MapGet("", async (IAuthorizationService auth, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(async () => await service.ListAsync(false, ct))).RequireAuthorization(Permission + "customer.view");
        admin.MapGet("/{id:guid}", async (Guid id, IAuthorizationService auth, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(async () => await service.GetAsync(id, false, ct))).RequireAuthorization(Permission + "customer.view");
        admin.MapPost("/{id:guid}/suspend", async (Guid id, StateChangeRequest request, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.ChangeStatusAsync(id, Domain.CustomerStatus.Suspended, request, UserId(user), ct))).RequireAuthorization(Permission + "customer.suspend");
        admin.MapPost("/{id:guid}/block", async (Guid id, StateChangeRequest request, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.ChangeStatusAsync(id, Domain.CustomerStatus.Blocked, request, UserId(user), ct))).RequireAuthorization(Permission + "customer.block");
        admin.MapPost("/{id:guid}/unblock", async (Guid id, StateChangeRequest request, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.ChangeStatusAsync(id, Domain.CustomerStatus.Active, request, UserId(user), ct))).RequireAuthorization(Permission + "customer.unblock");
        admin.MapPost("/{id:guid}/kyc/approve", async (Guid id, StateChangeRequest request, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.DecideKycAsync(id, true, request, UserId(user), ct))).RequireAuthorization(Permission + "kyc.review");
        admin.MapPost("/{id:guid}/kyc/reject", async (Guid id, StateChangeRequest request, ClaimsPrincipal user, ICustomerService service, CancellationToken ct) => await Run(() => service.DecideKycAsync(id, false, request, UserId(user), ct))).RequireAuthorization(Permission + "kyc.review");
        return endpoints;
    }
    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException());
    private static async Task<IResult> Run<T>(Func<Task<T>> action, bool created = false)
    {
        try { var result = await action(); return created ? Results.Created(string.Empty, result) : Results.Ok(result); }
        catch (CustomerRuleException ex) { return Results.Problem(statusCode: ex.StatusCode, title: ex.Message, extensions: new Dictionary<string, object?> { ["code"] = ex.Code }); }
    }
}
