using DigitalWallet.Wallets.Contracts;
using DigitalWallet.Wallets.Persistence;
using DigitalWallet.Wallets.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using DigitalWallet.Ledger.Contracts;

namespace DigitalWallet.Wallets;

public static class WalletsModule
{
    public static IServiceCollection AddWalletsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetRequiredSection("Platform:PostgreSql").Get<PlatformConnectionOptions>()?.ConnectionString
            ?? throw new InvalidOperationException("Platform PostgreSQL connection string is required.");
        services.AddDbContext<WalletsDbContext>(options => options.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", WalletsDbContext.Schema)));
        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<WalletProvisioning>();
        services.AddScoped<IWalletLedgerInventory, WalletLedgerInventory>();
        services.AddScoped<IPublicWalletLookup, Services.PublicWalletLookup>();
        if ((configuration["Hosting:Role"] is null or "Combined" or "Worker") && configuration.GetValue("Wallets:ProvisioningWorkerEnabled", true))
            services.AddHostedService<WalletProvisioningWorker>();
        services.AddScoped<IAdminWalletReader, AdminWalletReader>();
        return services;
    }
    public static async Task InitializeWalletsModuleAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    { using var scope = services.CreateScope(); await scope.ServiceProvider.GetRequiredService<WalletsDbContext>().Database.MigrateAsync(cancellationToken); }
}

internal sealed class PlatformConnectionOptions { public string ConnectionString { get; init; } = string.Empty; }
