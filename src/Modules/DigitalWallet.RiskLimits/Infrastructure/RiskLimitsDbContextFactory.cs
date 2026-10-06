using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace DigitalWallet.RiskLimits.Infrastructure;

public sealed class RiskLimitsDbContextFactory : IDesignTimeDbContextFactory<RiskLimitsDbContext>
{
    public RiskLimitsDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
        var connection = configuration["Platform:PostgreSql:ConnectionString"] ?? "Host=localhost;Database=digital_wallet;Username=postgres;Password=postgres";
        return new RiskLimitsDbContext(new DbContextOptionsBuilder<RiskLimitsDbContext>().UseNpgsql(connection,
            x => x.MigrationsHistoryTable("__EFMigrationsHistory", RiskLimitsDbContext.Schema)).Options);
    }
}
