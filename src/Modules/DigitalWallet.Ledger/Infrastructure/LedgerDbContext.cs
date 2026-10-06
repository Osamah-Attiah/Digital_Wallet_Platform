using DigitalWallet.Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DigitalWallet.Ledger.Infrastructure;

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public const string Schema = "ledger";
    public DbSet<LedgerAccount> Accounts => Set<LedgerAccount>();
    public DbSet<JournalEntry> Journals => Set<JournalEntry>();
    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();
    public DbSet<AccountBalance> Balances => Set<AccountBalance>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        const string currencyCheck = "\"Currency\" IN ('SAR','USD','EUR','GBP','JPY','AED','KWD','BHD','QAR','OMR')";
        builder.Entity<LedgerAccount>(b =>
        {
            b.ToTable("accounts", t =>
            {
                t.HasCheckConstraint("ck_account_currency", currencyCheck);
                t.HasCheckConstraint("ck_account_kind", "\"Kind\" IN ('CustomerWalletLiability','AdjustmentControl','PlatformFeeRevenue','Suspense')");
                t.HasCheckConstraint("ck_account_wallet", "(\"Kind\" = 'CustomerWalletLiability') = (\"WalletId\" IS NOT NULL)");
                t.HasCheckConstraint("ck_account_side", "(\"Kind\" IN ('CustomerWalletLiability','PlatformFeeRevenue') AND \"NormalSide\" = 'Credit') OR (\"Kind\" IN ('AdjustmentControl','Suspense') AND \"NormalSide\" = 'Debit')");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(100);
            b.Property(x => x.Currency).HasColumnType("character(3)");
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.NormalSide).HasConversion<string>().HasMaxLength(6);
            b.HasIndex(x => x.Code).IsUnique();
            b.HasIndex(x => new { x.WalletId, x.Currency }).IsUnique().HasFilter("\"WalletId\" IS NOT NULL");
            b.HasIndex(x => new { x.Kind, x.Currency }).IsUnique().HasFilter("\"WalletId\" IS NULL");
            b.HasAlternateKey(x => new { x.Id, x.Currency });
        });
        builder.Entity<JournalEntry>(b =>
        {
            b.ToTable("journals", t =>
            {
                t.HasCheckConstraint("ck_journal_currency", currencyCheck);
                t.HasCheckConstraint("ck_journal_status", "(\"Status\" = 'Draft' AND \"PostedAt\" IS NULL) OR (\"Status\" = 'Posted' AND \"PostedAt\" IS NOT NULL)");
                t.HasCheckConstraint("ck_journal_source", "length(btrim(\"SourceType\")) > 0 AND length(btrim(\"SourceId\")) > 0 AND length(btrim(\"Reason\")) > 0");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.SourceType).HasMaxLength(64);
            b.Property(x => x.SourceId).HasMaxLength(128);
            b.Property(x => x.RequestHash).HasMaxLength(64);
            b.Property(x => x.Reason).HasMaxLength(500);
            b.Property(x => x.Currency).HasColumnType("character(3)");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(6);
            b.HasIndex(x => new { x.SourceType, x.SourceId }).IsUnique();
            b.HasIndex(x => x.PostedAt);
            b.HasIndex(x => x.ReversalOf).IsUnique().HasFilter("\"ReversalOf\" IS NOT NULL");
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.ReversalOf).OnDelete(DeleteBehavior.Restrict);
            b.HasAlternateKey(x => new { x.Id, x.Currency });
        });
        builder.Entity<LedgerEntry>(b =>
        {
            b.ToTable("entries", t =>
            {
                t.HasCheckConstraint("ck_entry_positive", "\"AmountMinor\" > 0");
                t.HasCheckConstraint("ck_entry_side", "\"Side\" IN ('Debit','Credit')");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Side).HasConversion<string>().HasMaxLength(6);
            b.Property(x => x.Currency).HasColumnType("character(3)");
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.JournalId, x.Currency })
                .HasPrincipalKey(x => new { x.Id, x.Currency }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.AccountId, x.Currency })
                .HasPrincipalKey(x => new { x.Id, x.Currency }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.AccountId, x.JournalId });
        });
        builder.Entity<AccountBalance>(b =>
        {
            b.ToTable("account_balances", t => t.HasCheckConstraint("ck_balance_version", "\"Version\" >= 0"));
            b.HasKey(x => x.AccountId);
            b.HasOne<LedgerAccount>().WithOne().HasForeignKey<AccountBalance>(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class LedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<LedgerDbContext>()
        .UseNpgsql("Host=localhost;Database=digital_wallet;Username=postgres",
            x => x.MigrationsHistoryTable("__EFMigrationsHistory", LedgerDbContext.Schema)).Options);
}
