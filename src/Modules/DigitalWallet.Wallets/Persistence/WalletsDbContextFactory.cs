using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace DigitalWallet.Wallets.Persistence;
public sealed class WalletsDbContextFactory : IDesignTimeDbContextFactory<WalletsDbContext>
{ public WalletsDbContext CreateDbContext(string[] args) { var options = new DbContextOptionsBuilder<WalletsDbContext>(); options.UseNpgsql("Host=localhost;Port=5432;Database=digital_wallet;Username=postgres", x => x.MigrationsHistoryTable("__EFMigrationsHistory", WalletsDbContext.Schema)); return new WalletsDbContext(options.Options); } }
