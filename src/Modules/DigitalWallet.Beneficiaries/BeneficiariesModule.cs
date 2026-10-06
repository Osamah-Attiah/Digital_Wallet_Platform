using DigitalWallet.Beneficiaries.Application;
using DigitalWallet.Beneficiaries.Contracts;
using DigitalWallet.Beneficiaries.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalWallet.Beneficiaries;

public static class BeneficiariesModule
{
    public static IServiceCollection AddBeneficiariesModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetRequiredSection("Platform:PostgreSql").Get<PlatformConnectionOptions>()?.ConnectionString
            ?? throw new InvalidOperationException("Platform PostgreSQL connection string is required.");
        services.AddDbContext<BeneficiariesDbContext>(options => options.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", BeneficiariesDbContext.Schema)));
        services.AddOptions<BeneficiariesOptions>().Bind(configuration.GetSection(BeneficiariesOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<BeneficiaryService>();
        services.AddScoped<IBeneficiaryService>(provider => provider.GetRequiredService<BeneficiaryService>());
        services.AddScoped<IBeneficiaryReader>(provider => provider.GetRequiredService<BeneficiaryService>());
        return services;
    }

    public static async Task InitializeBeneficiariesModuleAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>().Database.MigrateAsync(cancellationToken);
    }

    private sealed class PlatformConnectionOptions { public string ConnectionString { get; init; } = string.Empty; }
}
