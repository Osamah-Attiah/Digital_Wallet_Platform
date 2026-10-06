using System.Security.Claims;
using DigitalWallet.Identity.Authorization;
using DigitalWallet.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalWallet.Identity;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth");
        auth.MapPost("/register", async (RegisterRequest request, IIdentityService service, HttpContext context, CancellationToken ct) =>
        {
            if (!request.AcceptTerms || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password)) return InvalidRequest();
            var result = await service.RegisterAsync(request, context, ct);
            return result.Accepted ? Results.Accepted($"/api/v1/auth/registration-challenges/{result.ChallengeId}", new { challengeId = result.ChallengeId }) : GenericAccepted();
        }).AllowAnonymous().RequireRateLimiting("identity-register");
        auth.MapPost("/register/verify", async (VerifyOtpRequest request, IIdentityService service, HttpContext context, CancellationToken ct) =>
        {
            var result = await service.VerifyRegistrationAsync(request, context, ct);
            return result is null ? Results.Problem(statusCode: 400, title: "OTP could not be verified.", extensions: new Dictionary<string, object?> { ["code"] = "OTP_INVALID" }) : Results.Ok(result);
        }).AllowAnonymous().RequireRateLimiting("identity-otp");
        auth.MapPost("/login", async (LoginRequest request, IIdentityService service, HttpContext context, CancellationToken ct) =>
        {
            var result = await service.LoginAsync(request, context, ct);
            return result is null ? Results.Problem(statusCode: 401, title: "Invalid credentials.", extensions: new Dictionary<string, object?> { ["code"] = "UNAUTHORIZED" }) : Results.Ok(result);
        }).AllowAnonymous().RequireRateLimiting("identity-login");
        auth.MapPost("/refresh", async (RefreshRequest request, IIdentityService service, HttpContext context, CancellationToken ct) =>
        {
            var result = await service.RefreshAsync(request, context, ct);
            return result is null ? Results.Problem(statusCode: 401, title: "Refresh token is invalid.", extensions: new Dictionary<string, object?> { ["code"] = "UNAUTHORIZED" }) : Results.Ok(result);
        }).AllowAnonymous().RequireRateLimiting("identity-refresh");
        auth.MapPost("/logout", async (RefreshRequest request, IIdentityService service, CancellationToken ct) => { await service.LogoutAsync(request.RefreshToken, ct); return Results.NoContent(); }).RequireRateLimiting("identity-refresh");
        auth.MapPost("/logout-all", async (ClaimsPrincipal principal, IIdentityService service, CancellationToken ct) => { await service.LogoutAllAsync(UserId(principal), ct); return Results.NoContent(); });
        auth.MapPost("/forgot-password", async (ForgotPasswordRequest request, IIdentityService service, CancellationToken ct) => { await service.ForgotPasswordAsync(request, ct); return GenericAccepted(); }).AllowAnonymous().RequireRateLimiting("identity-password");
        auth.MapPost("/reset-password", async (ResetPasswordRequest request, IIdentityService service, CancellationToken ct) =>
        {
            var complete = await service.ResetPasswordAsync(request, ct);
            return complete ? Results.NoContent() : Results.Problem(statusCode: 400, title: "Reset could not be completed.", extensions: new Dictionary<string, object?> { ["code"] = "OTP_INVALID" });
        }).AllowAnonymous().RequireRateLimiting("identity-password");
        endpoints.MapGet("/api/v1/devices", async (ClaimsPrincipal principal, IIdentityService service, CancellationToken ct) => Results.Ok(await service.ListDevicesAsync(UserId(principal), ct)));
        endpoints.MapDelete("/api/v1/devices/{deviceId:guid}", async (Guid deviceId, ClaimsPrincipal principal, IIdentityService service, CancellationToken ct) => await service.RevokeDeviceAsync(UserId(principal), deviceId, ct) ? Results.NoContent() : Results.NotFound());
        return endpoints;
    }
    private static Guid UserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue("sub") ?? throw new UnauthorizedAccessException());
    private static IResult GenericAccepted() => Results.Accepted(value: new { message = "If the request is eligible, a verification message will be sent." });
    private static IResult InvalidRequest() => Results.Problem(statusCode: 400, title: "The request is invalid.", extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
}
