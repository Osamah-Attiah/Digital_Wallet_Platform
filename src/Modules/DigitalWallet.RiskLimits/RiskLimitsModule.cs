using System.ComponentModel.DataAnnotations;
using DigitalWallet.RiskLimits.Application;
using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.RiskLimits.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalWallet.RiskLimits;

public sealed class RiskLimitsOptions
{
    [Range(1, 200)] public int MaxPageSize { get; init; } = 100;
    public string BusinessTimeZone { get; init; } = "Asia/Riyadh";
}
public static class RiskLimitsModule
{
    public static IServiceCollection AddRiskLimitsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration["Platform:PostgreSql:ConnectionString"] ?? throw new InvalidOperationException("PostgreSQL connection is required.");
        services.AddDbContext<RiskLimitsDbContext>(x => x.UseNpgsql(connection, n => n.MigrationsHistoryTable("__EFMigrationsHistory", RiskLimitsDbContext.Schema)));
        services.AddOptions<RiskLimitsOptions>().Bind(configuration.GetSection("RiskLimits")).ValidateDataAnnotations()
            .Validate(x => x.BusinessTimeZone == "Asia/Riyadh", "Phase 6 business timezone must be Asia/Riyadh.").ValidateOnStart();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<RiskLimitsStore>();
        services.AddScoped<ITransferAssessment>(sp => sp.GetRequiredService<RiskLimitsStore>());
        services.AddScoped<ILimitUsage>(sp => sp.GetRequiredService<RiskLimitsStore>());
        services.AddScoped<IRuleAdministration>(sp => sp.GetRequiredService<RiskLimitsStore>());
        return services;
    }
    public static async Task InitializeRiskLimitsModuleAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RiskLimitsDbContext>().Database.MigrateAsync(ct);
    }
}
