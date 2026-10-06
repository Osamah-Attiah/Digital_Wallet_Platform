using DigitalWallet.Transactions.Contracts;
using DigitalWallet.Transactions.Application;
using DigitalWallet.Transactions.Infrastructure;
using DigitalWallet.Transfers.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalWallet.Transactions;
public static class TransactionsModule
{
    public static IServiceCollection AddTransactionsModule(this IServiceCollection services, IConfiguration config)
    {
        var settings = config.GetSection("Transactions").Get<TransactionSettings>() ?? new();
        if (settings.MaxPageSize is < 1 or > 200 || settings.MaxHistoryDays is < 1 or > 366)
            throw new InvalidOperationException("Invalid transaction history settings.");
        services.AddSingleton(settings);
        services.AddDbContext<TransactionsDbContext>(x => x.UseNpgsql(config["Platform:PostgreSql:ConnectionString"],
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", TransactionsDbContext.Schema)));
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IReversalCheckpoint, NoopReversalCheckpoint>();
        services.AddScoped<ITransactionStore, TransactionStore>(); services.AddScoped<TransactionService>();
        services.AddScoped<IAdminTransactionReader, AdminTransactionReader>();
        return services;
    }
    public static async Task InitializeTransactionsModuleAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransactionsDbContext>();
        await db.Database.MigrateAsync(ct);
        var source = scope.ServiceProvider.GetRequiredService<ICompletedTransferReader>();
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        Guid? after = null;
        while (true)
        {
            var batch = await source.ListAsync(after, 200, ct); if (batch.Count == 0) break;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var fact in batch) await store.RecordAsync(fact, ct);
            await transaction.CommitAsync(ct); after = batch[^1].Id;
        }
    }
}
