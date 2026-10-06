using System.Text.Json;
using DigitalWallet.Identity;
using DigitalWallet.Customers;
using DigitalWallet.Ledger;
using DigitalWallet.Wallets;
using DigitalWallet.Beneficiaries;
using DigitalWallet.RiskLimits;
using DigitalWallet.Transfers;
using DigitalWallet.Transactions;
using DigitalWallet.Messaging;
using DigitalWallet.Notifications;
using DigitalWallet.Audit;
using DigitalWallet.Administration;
using DigitalWallet.Reporting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace DigitalWallet.Migrator;

public static class MigrationOrchestrator
{
    public const long LockId = 130013;
    public static readonly Type[] Contexts = [
        typeof(DigitalWallet.Identity.Persistence.IdentityDbContext), typeof(DigitalWallet.Customers.Persistence.CustomersDbContext),
        typeof(DigitalWallet.Ledger.Infrastructure.LedgerDbContext), typeof(DigitalWallet.Wallets.Persistence.WalletsDbContext),
        typeof(DigitalWallet.Beneficiaries.Infrastructure.BeneficiariesDbContext), typeof(DigitalWallet.RiskLimits.Infrastructure.RiskLimitsDbContext),
        typeof(DigitalWallet.Transfers.Infrastructure.TransfersDbContext), typeof(DigitalWallet.Transactions.Infrastructure.TransactionsDbContext),
        typeof(OutboxDbContext), typeof(NotificationsDbContext), typeof(AuditDbContext), typeof(AdministrationDbContext), typeof(ReportingDbContext) ];
    public static async Task RunAsync(IServiceProvider services, IConfiguration config, bool verify, CancellationToken ct)
    {
        // A separate, non-pooled session owns the lock for the WHOLE ordered plan, including seeds/grants.
        var cs = new NpgsqlConnectionStringBuilder(config["Platform:PostgreSql:ConnectionString"]) { Pooling = false };
        await using var connection = new NpgsqlConnection(cs.ConnectionString); await connection.OpenAsync(ct);
        await using var claim = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection) { CommandTimeout = 240 };
        claim.Parameters.AddWithValue("key", LockId); await claim.ExecuteNonQueryAsync(ct);
        try
        {
            if (!verify)
            {
                await services.InitializeIdentityModuleAsync(ct); await services.InitializeCustomersModuleAsync(ct);
                await services.InitializeLedgerModuleAsync(ct); await services.InitializeWalletsModuleAsync(ct);
                await services.InitializeBeneficiariesModuleAsync(ct); await services.InitializeRiskLimitsModuleAsync(ct);
                await services.InitializeTransfersModuleAsync(ct); await services.InitializeTransactionsModuleAsync(ct);
                await services.InitializeMessagingModuleAsync(ct); await services.InitializeNotificationsModuleAsync(ct);
                await services.InitializeAuditModuleAsync(ct); await services.InitializeAdministrationModuleAsync(ct);
                await services.InitializeReportingModuleAsync(ct);
                if (config.GetValue<bool>("Migration:ApplyRuntimeGrants"))
                {
                    var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "runtime-grants.sql"), ct);
                    await using var grantTransaction = await connection.BeginTransactionAsync(ct);
                    await using var grants = new NpgsqlCommand(sql, connection, grantTransaction); await grants.ExecuteNonQueryAsync(ct);
                    await grantTransaction.CommitAsync(ct);
                }
            }
            await using var scope = services.CreateAsyncScope(); var manifest = new List<object>();
            foreach (var type in Contexts)
            {
                var db = (DbContext)scope.ServiceProvider.GetRequiredService(type);
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
                if (pending.Length != 0) throw new InvalidOperationException("Pending migrations: " + type.Name);
                manifest.Add(new { context = type.Name, latest = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault(), pending = pending.Length });
            }
            Console.WriteLine(JsonSerializer.Serialize(new { status = "verified", version = config["Release:Version"] ?? "13.0.0", contexts = manifest }));
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            release.Parameters.AddWithValue("key", LockId); await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
