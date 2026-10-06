using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace DigitalWallet.Customers.Persistence;
public sealed class CustomersDbContextFactory : IDesignTimeDbContextFactory<CustomersDbContext>
{
    public CustomersDbContext CreateDbContext(string[] args) { var options = new DbContextOptionsBuilder<CustomersDbContext>(); options.UseNpgsql("Host=localhost;Port=5432;Database=digital_wallet;Username=postgres", x => x.MigrationsHistoryTable("__EFMigrationsHistory", CustomersDbContext.Schema)); return new CustomersDbContext(options.Options); }
}
