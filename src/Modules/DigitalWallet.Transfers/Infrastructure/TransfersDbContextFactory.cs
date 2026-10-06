using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DigitalWallet.Transfers.Infrastructure;
public sealed class TransfersDbContextFactory : IDesignTimeDbContextFactory<TransfersDbContext>
{
    public TransfersDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TransfersDbContext>()
        .UseNpgsql("Host=localhost;Database=digital_wallet_design", x => x.MigrationsHistoryTable("__EFMigrationsHistory", TransfersDbContext.Schema)).Options);
}
