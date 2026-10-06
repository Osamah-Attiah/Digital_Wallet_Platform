using DigitalWallet.Transfers.Domain;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Transfers.Infrastructure;

public sealed class TransfersDbContext(DbContextOptions<TransfersDbContext> options) : DbContext(options)
{
    public const string Schema = "transfers";
    public DbSet<TransferQuote> Quotes => Set<TransferQuote>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<TransferIdempotency> Idempotency => Set<TransferIdempotency>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<TransferQuote>(b =>
        {
            b.ToTable("quotes", t =>
            {
                t.HasCheckConstraint("CK_quotes_money", "\"AmountMinor\" > 0 AND \"FeeMinor\" >= 0 AND \"TotalDebitMinor\" > 0 AND \"TotalDebitMinor\"::numeric = \"AmountMinor\"::numeric + \"FeeMinor\"::numeric");
                t.HasCheckConstraint("CK_quotes_wallets", "\"SenderWalletId\" <> \"TargetWalletId\"");
                t.HasCheckConstraint("CK_quotes_time", "\"ExpiresAt\" > \"CreatedAt\" AND (\"UsedAt\" IS NULL OR (\"UsedAt\" >= \"CreatedAt\" AND \"UsedAt\" < \"ExpiresAt\"))");
                t.HasCheckConstraint("CK_quotes_values", "\"Currency\" = 'SAR' AND \"RiskDecision\" IN ('Allow','Review','Deny') AND \"Version\" > 0 AND \"FeeRuleVersion\" > 0");
            });
            b.HasKey(x => x.Id); b.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();
            b.Property(x => x.Currency).HasMaxLength(3); b.Property(x => x.RiskDecision).HasMaxLength(16);
            b.Property(x => x.RequestFingerprint).HasMaxLength(64);
            b.Property(x => x.MaskedSenderName).HasMaxLength(100); b.Property(x => x.MaskedRecipientName).HasMaxLength(100);
            b.HasIndex(x => new { x.OwnerCustomerId, x.ExpiresAt });
        });
        model.Entity<Transfer>(b =>
        {
            b.ToTable("transfers", t =>
            {
                t.HasCheckConstraint("CK_transfers_money", "\"AmountMinor\" > 0 AND \"FeeMinor\" >= 0 AND \"TotalDebitMinor\" > 0 AND \"TotalDebitMinor\"::numeric = \"AmountMinor\"::numeric + \"FeeMinor\"::numeric");
                t.HasCheckConstraint("CK_transfers_wallets", "\"SenderWalletId\" <> \"TargetWalletId\" AND \"OwnerCustomerId\" <> \"TargetCustomerId\"");
                t.HasCheckConstraint("CK_transfers_state", "\"Status\" IN ('PendingConfirmation','Validating','PendingReview','Completed','Rejected','Failed') AND (\"Status\" = 'Completed') = (\"JournalId\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL) AND (\"Status\" = 'Completed' OR (\"JournalId\" IS NULL AND \"CompletedAt\" IS NULL))");
                t.HasCheckConstraint("CK_transfers_time", "\"ExpiresAt\" > \"CreatedAt\" AND (\"CompletedAt\" IS NULL OR (\"CompletedAt\" >= \"CreatedAt\" AND \"CompletedAt\" < \"ExpiresAt\"))");
                t.HasCheckConstraint("CK_transfers_confirmation", "\"Status\" NOT IN ('Validating','Completed','PendingReview','Rejected') OR (\"ConfirmedAt\" IS NOT NULL AND \"OtpChallengeId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_transfers_values", "\"Currency\" = 'SAR' AND \"Version\" > 0");
            });
            b.HasKey(x => x.Id); b.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Currency).HasMaxLength(3); b.Property(x => x.PublicReference).HasMaxLength(40);
            b.Property(x => x.Note).HasMaxLength(500); b.Property(x => x.ReasonCode).HasMaxLength(64);
            b.Property(x => x.MaskedSenderName).HasMaxLength(100); b.Property(x => x.MaskedRecipientName).HasMaxLength(100);
            b.HasIndex(x => x.PublicReference).IsUnique(); b.HasIndex(x => x.QuoteId).IsUnique(); b.HasIndex(x => x.JournalId).IsUnique();
            b.HasIndex(x => new { x.OwnerCustomerId, x.CreatedAt, x.Id }); b.HasIndex(x => new { x.TargetCustomerId, x.CreatedAt, x.Id });
            b.HasOne<TransferQuote>().WithOne().HasForeignKey<Transfer>(x => x.QuoteId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<TransferIdempotency>(b =>
        {
            b.ToTable("idempotency"); b.HasKey(x => x.Id);
            b.Property(x => x.Scope).HasMaxLength(64); b.Property(x => x.Key).HasMaxLength(128); b.Property(x => x.RequestHash).HasMaxLength(64);
            b.HasIndex(x => new { x.OwnerCustomerId, x.Scope, x.Key }).IsUnique(); b.HasIndex(x => x.TransferId).IsUnique();
            b.HasOne<Transfer>().WithOne().HasForeignKey<TransferIdempotency>(x => x.TransferId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
