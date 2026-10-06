using DigitalWallet.Transactions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DigitalWallet.Transactions.Infrastructure;
public sealed class TransactionsDbContext(DbContextOptions<TransactionsDbContext> options) : DbContext(options)
{
    public const string Schema = "transactions";
    public DbSet<FinancialTransaction> Transactions => Set<FinancialTransaction>();
    public DbSet<ReversalRequest> ReversalRequests => Set<ReversalRequest>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<FinancialTransaction>(b =>
        {
            b.ToTable("transactions", t =>
            {
                t.HasCheckConstraint("CK_transactions_money", "\"AmountMinor\" > 0 AND \"FeeMinor\" >= 0 AND \"SenderDebitMinor\" > 0 AND \"RecipientCreditMinor\" > 0 AND ((\"Type\" = 'InternalTransfer' AND \"SenderDebitMinor\"::numeric = \"AmountMinor\"::numeric + \"FeeMinor\"::numeric AND \"RecipientCreditMinor\" = \"AmountMinor\") OR (\"Type\" = 'Reversal' AND \"SenderDebitMinor\" = \"AmountMinor\" AND \"RecipientCreditMinor\"::numeric = \"AmountMinor\"::numeric + \"FeeMinor\"::numeric))");
                t.HasCheckConstraint("CK_transactions_values", "\"Currency\" = 'SAR' AND \"SenderCustomerId\" <> \"RecipientCustomerId\" AND \"SenderWalletId\" <> \"RecipientWalletId\" AND ((\"Type\" = 'InternalTransfer' AND \"ReversalOf\" IS NULL AND \"Id\" = \"TransferId\") OR (\"Type\" = 'Reversal' AND \"ReversalOf\" IS NOT NULL AND \"ReversalOf\" <> \"Id\"))");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.PublicReference).HasMaxLength(40); b.Property(x => x.Type).HasMaxLength(32); b.Property(x => x.Currency).HasMaxLength(3);
            b.Property(x => x.MaskedSenderName).HasMaxLength(100); b.Property(x => x.MaskedRecipientName).HasMaxLength(100);
            b.HasIndex(x => x.PublicReference).IsUnique(); b.HasIndex(x => x.JournalId).IsUnique(); b.HasIndex(x => x.ReversalOf).IsUnique();
            b.HasIndex(x => new { x.SenderCustomerId, x.OccurredAt, x.Id }); b.HasIndex(x => new { x.RecipientCustomerId, x.OccurredAt, x.Id });
            b.HasIndex(x => new { x.OccurredAt, x.Id });
            b.HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => x.ReversalOf).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ReversalRequest>(b =>
        {
            b.ToTable("reversal_requests", t =>
            {
                t.HasCheckConstraint("CK_reversal_requests_reason", "length(btrim(\"Reason\")) > 0 AND length(\"RequestKey\") > 0");
                t.HasCheckConstraint("CK_reversal_requests_state", "(\"Status\" = 'Pending' AND \"DecidedBy\" IS NULL AND \"DecidedAt\" IS NULL AND \"DecisionNote\" IS NULL AND \"ApprovalKey\" IS NULL AND \"ApprovalHash\" IS NULL AND \"OtpChallengeId\" IS NULL AND \"ReversalTransactionId\" IS NULL) OR (\"Status\" IN ('Completed','Rejected') AND \"DecidedBy\" IS NOT NULL AND \"DecidedBy\" <> \"RequestedBy\" AND \"DecidedAt\" IS NOT NULL AND \"DecidedAt\" >= \"CreatedAt\" AND ((\"Status\" = 'Completed' AND \"ApprovalKey\" IS NOT NULL AND \"ApprovalHash\" IS NOT NULL AND \"OtpChallengeId\" IS NOT NULL AND \"ReversalTransactionId\" IS NOT NULL AND \"ReversalTransactionId\" = \"Id\") OR (\"Status\" = 'Rejected' AND \"DecisionNote\" IS NOT NULL AND length(btrim(\"DecisionNote\")) > 0 AND \"ReversalTransactionId\" IS NULL AND \"ApprovalKey\" IS NULL AND \"ApprovalHash\" IS NULL AND \"OtpChallengeId\" IS NULL)))");
            });
            b.HasKey(x => x.Id); b.Property(x => x.Status).HasMaxLength(32); b.Property(x => x.Reason).HasMaxLength(500);
            b.Property(x => x.DecisionNote).HasMaxLength(500); b.Property(x => x.RequestKey).HasMaxLength(128); b.Property(x => x.RequestHash).HasMaxLength(64);
            b.Property(x => x.ApprovalKey).HasMaxLength(128); b.Property(x => x.ApprovalHash).HasMaxLength(64);
            b.HasIndex(x => new { x.RequestedBy, x.RequestKey }).IsUnique(); b.HasIndex(x => new { x.DecidedBy, x.ApprovalKey }).IsUnique();
            b.HasIndex(x => x.OriginalTransactionId).IsUnique().HasFilter("\"Status\" IN ('Pending','Completed')");
            b.HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => x.OriginalTransactionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => x.ReversalTransactionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
public sealed class TransactionsDbContextFactory : IDesignTimeDbContextFactory<TransactionsDbContext>
{
    public TransactionsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TransactionsDbContext>()
        .UseNpgsql("Host=localhost;Database=digital_wallet_design", n => n.MigrationsHistoryTable("__EFMigrationsHistory", TransactionsDbContext.Schema)).Options);
}
