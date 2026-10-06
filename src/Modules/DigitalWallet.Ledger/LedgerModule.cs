using DigitalWallet.Ledger.Application;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Ledger.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalWallet.Ledger;

public static class LedgerModule
{
    public static IServiceCollection AddLedgerModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<LedgerDbContext>((_, options) => options.UseNpgsql(
            configuration["Platform:PostgreSql:ConnectionString"],
            x => x.MigrationsHistoryTable("__EFMigrationsHistory", LedgerDbContext.Schema)));
        services.AddScoped<LedgerStore>();
        services.AddScoped<ILedgerStore>(sp => sp.GetRequiredService<LedgerStore>());
        services.AddScoped<ILedgerAccounts>(sp => sp.GetRequiredService<LedgerStore>());
        services.AddScoped<ILedgerReader>(sp => sp.GetRequiredService<LedgerStore>());
        services.AddScoped<ILedgerPosting, LedgerPostingService>();
        services.AddScoped<ILedgerReconciliation, LedgerReconciliationService>();
        return services;
    }

    public static async Task InitializeLedgerModuleAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync(ct);
        var accounts = scope.ServiceProvider.GetRequiredService<ILedgerAccounts>();
        foreach (var kind in Enum.GetValues<SystemAccount>())
            await accounts.EnsureSystemAccountAsync(kind, "SAR", ct);
    }
}
