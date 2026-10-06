using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace DigitalWallet.Beneficiaries.Infrastructure;

public sealed class BeneficiariesDbContextFactory : IDesignTimeDbContextFactory<BeneficiariesDbContext>
{
    public BeneficiariesDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
        var connection = configuration["Platform:PostgreSql:ConnectionString"] ?? "Host=localhost;Database=digital_wallet;Username=postgres;Password=postgres";
        return new BeneficiariesDbContext(new DbContextOptionsBuilder<BeneficiariesDbContext>().UseNpgsql(connection,
            x => x.MigrationsHistoryTable("__EFMigrationsHistory", BeneficiariesDbContext.Schema)).Options);
    }
}
