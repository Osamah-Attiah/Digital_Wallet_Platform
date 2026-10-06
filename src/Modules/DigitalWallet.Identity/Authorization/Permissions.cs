using System.Security.Claims;
using DigitalWallet.Identity.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DigitalWallet.Identity.Authorization;

public static class Permissions
{
    public const string Prefix = "Permission:";
    public static readonly string[] All = [
        "profile.view.own", "profile.edit.own", "wallet.create.own", "wallet.view.own", "wallet.view.any", "wallet.freeze", "wallet.unfreeze",
        "beneficiary.manage.own", "transfer.create", "transfer.view.own", "transfer.view.any", "transaction.view.own", "transaction.view.any",
        "transaction.reverse.request", "transaction.reverse.approve", "customer.view", "customer.pii.view", "customer.suspend", "customer.unblock",
        "customer.block", "kyc.review", "notification.manage.own", "report.view", "report.generate", "audit.view", "staff.manage", "role.manage", "permission.assign", "configuration.view", "configuration.manage", "operations.view", "risk.rules.create", "risk.rules.publish", "risk.rules.read"
    ];
}

public sealed record PermissionRequirement(string Code) : IAuthorizationRequirement;

public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => policyName.StartsWith(Permissions.Prefix, StringComparison.Ordinal)
        ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder().AddRequirements(new PermissionRequirement(policyName[Permissions.Prefix.Length..])).Build())
        : base.GetPolicyAsync(policyName);
}

public interface IPermissionResolver { Task<bool> HasAsync(Guid userId, string permission, CancellationToken cancellationToken); }
public sealed class PermissionResolver(IdentityDbContext context, DigitalWallet.Identity.Services.EffectivePermissions effective, DigitalWallet.Caching.Contracts.ISafeCache cache, Microsoft.AspNetCore.Http.IHttpContextAccessor http) : IPermissionResolver
{
    public async Task<bool> HasAsync(Guid userId, string permission, CancellationToken cancellationToken)
    {
        // Sensitive data and report authorization are always evaluated in PostgreSQL.
        var path = http.HttpContext?.Request.Path.Value ?? "";
        var sensitiveData = path.StartsWith("/api/v1/admin/reports", StringComparison.Ordinal) || path.EndsWith("/pii", StringComparison.Ordinal);
        if (cache.Enabled && !sensitiveData && !permission.EndsWith(".own", StringComparison.Ordinal) && permission != "transfer.create" && permission != "customer.pii.view" && !permission.StartsWith("report.", StringComparison.Ordinal) && context.Database.CurrentTransaction == null)
            return (await effective.ResolveAsync(userId, cancellationToken)).Contains(permission);
        return await (from userRole in context.UserRoles
         join rolePermission in context.RolePermissions on userRole.RoleId equals rolePermission.RoleId
         where userRole.UserId == userId && rolePermission.PermissionCode == permission
         select rolePermission).AnyAsync(cancellationToken);
    }
}

public sealed class PermissionAuthorizationHandler(IPermissionResolver permissions) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var rawId = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(rawId, out var userId) && await permissions.HasAsync(userId, requirement.Code, CancellationToken.None)) context.Succeed(requirement);
    }
}

public sealed record DeviceResource(Guid OwnerUserId);
public sealed class DeviceOwnershipRequirement : IAuthorizationRequirement;
public sealed class DeviceOwnershipAuthorizationHandler : AuthorizationHandler<DeviceOwnershipRequirement, DeviceResource>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, DeviceOwnershipRequirement requirement, DeviceResource resource)
    {
        var rawId = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(rawId, out var userId) && userId == resource.OwnerUserId) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
