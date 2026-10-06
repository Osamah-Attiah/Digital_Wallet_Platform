using DigitalWallet.Customers.Domain;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Wallets.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace DigitalWallet.Migrator;

// Explicit operator job, never registered as an HTTP route or background service.
public static class SyntheticDemoSeed
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration configuration, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("SyntheticDemo:Enabled") || configuration["SyntheticDemo:DataClassification"] != "synthetic-only")
            throw new InvalidOperationException("Synthetic seed is restricted to the isolated demo deployment.");
        var cs = new NpgsqlConnectionStringBuilder(configuration["Platform:PostgreSql:ConnectionString"]) { Pooling = false };
        await using var connection = new NpgsqlConnection(cs.ConnectionString); await connection.OpenAsync(ct);
        await using var claim = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection);
        claim.Parameters.AddWithValue("key", MigrationOrchestrator.LockId); await claim.ExecuteNonQueryAsync(ct);
        try
        {
            await using var scope = services.CreateAsyncScope(); var sp = scope.ServiceProvider;
            var identity = sp.GetRequiredService<IdentityDbContext>();
            if (await identity.Users.AnyAsync(x => !x.Contact.EndsWith("@demo.invalid"), ct))
                throw new InvalidOperationException("The demo seed refuses a database containing non-demo identities.");
            var manager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var customers = sp.GetRequiredService<CustomersDbContext>();
            foreach (var actor in new[] { "maker", "checker", "customer_a", "customer_b" })
            {
                var contact = actor + "@demo.invalid";
                var user = await manager.FindByEmailAsync(contact);
                if (user is null)
                {
                    user = new ApplicationUser { Id = Guid.NewGuid(), UserName = contact, Email = contact, EmailConfirmed = true, Contact = contact, NormalizedContact = contact.ToUpperInvariant(), AcceptedTerms = true };
                    var password = (await File.ReadAllTextAsync(Path.Combine(configuration["Hosting:SecretsDirectory"]!, "demo_" + actor + "_password"), ct)).Trim();
                    var result = await manager.CreateAsync(user, password);
                    if (!result.Succeeded) throw new InvalidOperationException("Synthetic identity creation failed.");
                }
                var roleName = actor is "maker" or "checker" ? "Demo" + actor : "Customer";
                if (actor is "maker" or "checker")
                {
                    var role = await identity.Roles.SingleOrDefaultAsync(x => x.Name == roleName, ct);
                    if (role is null)
                    {
                        role = new ApplicationRole { Id = Guid.NewGuid(), Name = roleName, NormalizedName = roleName.ToUpperInvariant(), DisplayName = "Synthetic demo " + actor };
                        identity.Roles.Add(role);
                        var permissions = new[] { "customer.view", "wallet.view.any", "transfer.view.any", "transaction.view.any", "report.view", "report.generate", "audit.view", "operations.view", "risk.rules.read" }
                            .Concat(actor == "maker" ? ["transaction.reverse.request", "risk.rules.create", "risk.rules.publish"] : new[] { "transaction.reverse.approve" });
                        foreach (var permission in permissions) identity.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = permission });
                        await identity.SaveChangesAsync(ct);
                    }
                }
                if (!await manager.IsInRoleAsync(user, roleName))
                {
                    if (!(await manager.AddToRoleAsync(user, roleName)).Succeeded) throw new InvalidOperationException("Synthetic role assignment failed.");
                    user.AuthorizationVersion++; await identity.SaveChangesAsync(ct);
                }
                if (actor is "maker" or "checker") continue;
                if (!await customers.Customers.AnyAsync(x => x.IdentityUserId == user.Id, ct))
                {
                    customers.Customers.Add(new Customer { IdentityUserId = user.Id, FirstName = "Synthetic", LastName = actor, Status = CustomerStatus.Active, KycStatus = KycStatus.Verified });
                    await customers.SaveChangesAsync(ct);
                }
                var wallet = await sp.GetRequiredService<IWalletService>().CreateAsync(user.Id, new CreateWalletRequest(), ct);
                var accounts = sp.GetRequiredService<ILedgerAccounts>();
                var account = await accounts.EnsureWalletAccountAsync(wallet.Id, "SAR", ct);
                var control = await accounts.EnsureSystemAccountAsync(SystemAccount.AdjustmentControl, "SAR", ct);
                await sp.GetRequiredService<ILedgerPosting>().PostAsync(new PostJournal("SyntheticDemoSeed", actor + "-v1", "SAR",
                    [new(control, PostingSide.Debit, 10000), new(account, PostingSide.Credit, 10000)], "Synthetic units only; no real funds", user.Id), ct);
            }
            var reconciliation = await sp.GetRequiredService<ILedgerReconciliation>().RunAsync(ct);
            if (reconciliation.Findings.Count != 0) throw new InvalidOperationException("Synthetic seed reconciliation failed.");
            Console.WriteLine("{\"syntheticSeed\":\"passed\",\"reconciliationFindings\":0}");
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            release.Parameters.AddWithValue("key", MigrationOrchestrator.LockId); await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
