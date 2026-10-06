using DigitalWallet.Transfers.Application;
using DigitalWallet.Transfers.Contracts;
using DigitalWallet.Transfers.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalWallet.Transfers;
public static class TransfersModule
{
    public static IServiceCollection AddTransfersModule(this IServiceCollection services, IConfiguration config)
    {
        var settings = config.GetSection("Transfers").Get<TransferSettings>() ?? new();
        if (settings.QuoteLifetimeSeconds is < 30 or > 900 || settings.MaxPageSize is < 1 or > 200)
            throw new InvalidOperationException("Invalid transfer lifetime or page size.");
        services.AddSingleton(settings);
        services.AddDbContext<TransfersDbContext>(x => x.UseNpgsql(config["Platform:PostgreSql:ConnectionString"],
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", TransfersDbContext.Schema)));
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<ITransferCheckpoint, NoopTransferCheckpoint>();
        services.AddScoped<ITransferQueries, TransfersStore>();
        services.AddScoped<ICompletedTransferReader, TransfersStore>();
        services.AddScoped<TransferService>();
        services.AddScoped<ITransferReader>(sp => sp.GetRequiredService<TransferService>());
        services.AddScoped<ITransferReportReader, TransferReportReader>();
        return services;
    }
    public static async Task InitializeTransfersModuleAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TransfersDbContext>().Database.MigrateAsync(ct);
    }
}
