using DigitalWallet.Customers.Contracts;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Customers.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalWallet.Customers;

public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetRequiredSection("Platform:PostgreSql").Get<PlatformConnectionOptions>()?.ConnectionString
            ?? throw new InvalidOperationException("Platform PostgreSQL connection string is required.");
        services.AddDbContext<CustomersDbContext>(options => options.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", CustomersDbContext.Schema)));
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<ICustomerEligibilityReader, CustomerEligibilityReader>();
        services.AddScoped<ICustomerRecipientReader, CustomerRecipientReader>();
        services.AddScoped<IAdminCustomerReader, AdminCustomerReader>();
        return services;
    }

    public static async Task InitializeCustomersModuleAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CustomersDbContext>().Database.MigrateAsync(cancellationToken);
    }
}

internal sealed class PlatformConnectionOptions { public string ConnectionString { get; init; } = string.Empty; }
