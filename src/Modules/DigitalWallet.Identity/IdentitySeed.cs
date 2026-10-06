using DigitalWallet.Identity.Authorization;
using DigitalWallet.Identity.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalWallet.Identity;

internal static class IdentitySeed
{
    public static async Task EnsureAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<IdentityDbContext>();
        foreach (var code in Permissions.All.Where(code => !context.Permissions.Any(existing => existing.Code == code)))
            context.Permissions.Add(new Permission { Code = code, Description = code });
        await context.SaveChangesAsync(cancellationToken);
        var role = await context.Roles.Where(x => x.NormalizedName == "CUSTOMER").SingleOrDefaultAsync(cancellationToken);
        if (role is null)
        {
            role = new ApplicationRole { Id = Guid.NewGuid(), Name = "Customer", NormalizedName = "CUSTOMER", DisplayName = "Customer" };
            context.Roles.Add(role);
            await context.SaveChangesAsync(cancellationToken);
        }
        foreach (var code in new[] { "profile.view.own", "profile.edit.own", "wallet.create.own", "wallet.view.own", "beneficiary.manage.own", "transfer.create", "transfer.view.own", "transaction.view.own", "notification.manage.own" }.Where(code => !context.RolePermissions.Any(x => x.RoleId == role.Id && x.PermissionCode == code)))
            context.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = code });
        await context.SaveChangesAsync(cancellationToken);
    }
}
