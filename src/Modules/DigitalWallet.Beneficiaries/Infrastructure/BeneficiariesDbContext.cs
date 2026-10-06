using DigitalWallet.Beneficiaries.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DigitalWallet.Beneficiaries.Infrastructure;

public sealed class BeneficiariesDbContext(DbContextOptions<BeneficiariesDbContext> options) : DbContext(options)
{
    public const string Schema = "beneficiaries";
    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        builder.Entity<Beneficiary>(b =>
        {
            b.ToTable("beneficiaries", table =>
            {
                table.HasCheckConstraint("CK_beneficiaries_state_timestamps", """
                    ("Status" = 'PendingVerification' AND "VerifiedAt" IS NULL AND "ActivateAfter" IS NULL AND "ActivatedAt" IS NULL AND "DeletedAt" IS NULL)
                    OR ("Status" = 'CoolingOff' AND "VerifiedAt" IS NOT NULL AND "ActivateAfter" IS NOT NULL AND "ActivatedAt" IS NULL AND "DeletedAt" IS NULL)
                    OR ("Status" = 'Active' AND "VerifiedAt" IS NOT NULL AND "ActivateAfter" IS NOT NULL AND "ActivatedAt" IS NOT NULL AND "DeletedAt" IS NULL)
                    OR ("Status" = 'Deleted' AND "DeletedAt" IS NOT NULL)
                    """);
                table.HasCheckConstraint("CK_beneficiaries_timestamp_order", """
                    "CreatedAt" <= "UpdatedAt"
                    AND ("VerifiedAt" IS NULL OR "VerifiedAt" >= "CreatedAt")
                    AND ("ActivateAfter" IS NULL OR ("VerifiedAt" IS NOT NULL AND "ActivateAfter" >= "VerifiedAt"))
                    AND ("ActivatedAt" IS NULL OR ("VerifiedAt" IS NOT NULL AND "ActivatedAt" >= "VerifiedAt"))
                    AND ("DeletedAt" IS NULL OR "DeletedAt" >= "CreatedAt")
                    """);
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.TargetWalletId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            b.Property(x => x.Nickname).HasMaxLength(100).IsRequired();
            b.Property(x => x.MaskedRecipientName).HasMaxLength(64).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();
            b.HasIndex(x => new { x.OwnerCustomerId, x.TargetWalletId })
                .IsUnique().HasFilter("\"DeletedAt\" IS NULL")
                .HasDatabaseName("UX_beneficiaries_owner_target_live");
            b.HasIndex(x => new { x.OwnerCustomerId, x.DeletedAt, x.CreatedAt, x.Id })
                .HasDatabaseName("IX_beneficiaries_owner_page");
        });
    }
}
