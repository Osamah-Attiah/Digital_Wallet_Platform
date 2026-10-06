using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Wallets.Domain;
using DigitalWallet.Wallets.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalWallet.Wallets.Services;

public sealed class WalletProvisioning(WalletsDbContext db, ILedgerAccounts ledger, ILogger<WalletProvisioning> logger)
{
    public async Task<Wallet> EnsureAsync(Guid id, Guid? actor, CancellationToken ct = default)
    {
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        if (wallet.LedgerProvisionedAt is not null) return wallet;
        // Pending row is committed BEFORE the external module call. It is the durable retry intent.
        try { await ledger.EnsureWalletAccountAsync(wallet.Id, wallet.Currency, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Ledger provisioning retry: {ErrorCode}", "DEPENDENCY_UNAVAILABLE");
            return wallet;
        }
        // A crash here is recoverable: the same wallet/currency always resolves to the same account.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.Wallets.FromSqlInterpolated($"SELECT * FROM wallets.wallets WHERE \"Id\" = {id} FOR UPDATE")
            .AsNoTracking().SingleAsync(ct);
        if (locked.LedgerProvisionedAt is null)
        {
            db.Attach(locked);
            locked.LedgerProvisionedAt = DateTimeOffset.UtcNow;
            if (locked.Status == WalletStatus.Pending)
            {
                locked.Status = WalletStatus.Active;
                db.WalletStatusHistory.Add(new WalletStatusHistory
                {
                    WalletId = id, FromStatus = WalletStatus.Pending, ToStatus = WalletStatus.Active,
                    Reason = "Ledger account provisioned", ActorIdentityUserId = actor
                });
            }
            locked.UpdatedAt = DateTimeOffset.UtcNow;
            locked.ConcurrencyVersion = Guid.NewGuid();
            await db.SaveChangesAsync(ct);
            db.Entry(locked).State = EntityState.Detached;
        }
        await tx.CommitAsync(ct);
        return locked;
    }

    public async Task<int> RetryBatchAsync(CancellationToken ct = default)
    {
        var ids = await db.Wallets.AsNoTracking().Where(x => x.LedgerProvisionedAt == null)
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).Take(100).Select(x => x.Id).ToListAsync(ct);
        var completed = 0;
        foreach (var id in ids)
        {
            if ((await EnsureAsync(id, null, ct)).LedgerProvisionedAt is not null) completed++;
            else
            {
                // Move failed items to the end of the retry queue so a bad item cannot starve later wallets.
                await db.Wallets.Where(x => x.Id == id && x.LedgerProvisionedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            }
        }
        return completed;
    }
}

public sealed class WalletLedgerInventory(WalletsDbContext db) : IWalletLedgerInventory
{
    public async Task<IReadOnlyList<WalletLedgerReference>> ListAsync(CancellationToken ct = default) =>
        await db.Wallets.AsNoTracking().Select(x => new WalletLedgerReference(x.Id, x.Currency)).ToListAsync(ct);
}

public sealed class WalletProvisioningWorker(IServiceScopeFactory scopes, ILogger<WalletProvisioningWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<WalletProvisioning>().RetryBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogError("Wallet ledger provisioning batch failed: {ErrorCode}", "DEPENDENCY_UNAVAILABLE"); }
        }
    }
}
