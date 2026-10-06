using DigitalWallet.Wallets.Domain;
using Microsoft.EntityFrameworkCore;
namespace DigitalWallet.Wallets.Persistence;
public sealed class WalletsDbContext(DbContextOptions<WalletsDbContext> options) : DbContext(options)
{
    public const string Schema = "wallets";
    public DbSet<Wallet> Wallets => Set<Wallet>(); public DbSet<WalletStatusHistory> WalletStatusHistory => Set<WalletStatusHistory>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        builder.Entity<Wallet>().ToTable("wallets", t => t.HasCheckConstraint("ck_wallet_active_has_ledger",
            "\"Status\" <> 'Active' OR \"LedgerProvisionedAt\" IS NOT NULL"));
        builder.Entity<Wallet>().HasIndex(x => x.Id).HasFilter("\"LedgerProvisionedAt\" IS NULL").HasDatabaseName("IX_wallets_pending_ledger");
        builder.Entity<Wallet>(b => { b.ToTable("wallets"); b.HasKey(x => x.Id); b.Property(x => x.Currency).HasMaxLength(3).IsRequired(); b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32); b.Property(x => x.PublicHandle).HasMaxLength(32).IsRequired(); b.HasIndex(x => new { x.CustomerId, x.Currency }).IsUnique(); b.HasIndex(x => x.PublicHandle).IsUnique(); b.Property(x => x.ConcurrencyVersion).IsConcurrencyToken(); });
        builder.Entity<WalletStatusHistory>(b => { b.ToTable("wallet_status_history"); b.HasKey(x => x.Id); b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(32); b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(32); b.Property(x => x.Reason).HasMaxLength(500).IsRequired(); b.HasIndex(x => new { x.WalletId, x.OccurredAt }); });
    }
}
