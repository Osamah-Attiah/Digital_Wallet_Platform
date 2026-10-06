using DigitalWallet.Beneficiaries.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Beneficiaries.Infrastructure.Migrations;

[DbContext(typeof(BeneficiariesDbContext))]
[Migration("20260920180500_InitialBeneficiaries")]
public sealed class InitialBeneficiaries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "beneficiaries");
        migrationBuilder.CreateTable(
            name: "beneficiaries", schema: "beneficiaries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                TargetWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                Nickname = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                MaskedRecipientName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                OtpChallengeId = table.Column<Guid>(type: "uuid", nullable: true),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ActivateAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyVersion = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_beneficiaries", x => x.Id));
        migrationBuilder.CreateIndex(name: "UX_beneficiaries_owner_target_live", schema: "beneficiaries", table: "beneficiaries", columns: new[] { "OwnerCustomerId", "TargetWalletId" }, unique: true, filter: "\"DeletedAt\" IS NULL");
        migrationBuilder.CreateIndex(name: "IX_beneficiaries_owner_page", schema: "beneficiaries", table: "beneficiaries", columns: new[] { "OwnerCustomerId", "DeletedAt", "CreatedAt", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "beneficiaries", schema: "beneficiaries");
}
