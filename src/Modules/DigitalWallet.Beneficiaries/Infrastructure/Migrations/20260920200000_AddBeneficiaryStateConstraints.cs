using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Beneficiaries.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBeneficiaryStateConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_beneficiaries_state_timestamps",
                schema: "beneficiaries",
                table: "beneficiaries",
                sql: "(\"Status\" = 'PendingVerification' AND \"VerifiedAt\" IS NULL AND \"ActivateAfter\" IS NULL AND \"ActivatedAt\" IS NULL AND \"DeletedAt\" IS NULL)\nOR (\"Status\" = 'CoolingOff' AND \"VerifiedAt\" IS NOT NULL AND \"ActivateAfter\" IS NOT NULL AND \"ActivatedAt\" IS NULL AND \"DeletedAt\" IS NULL)\nOR (\"Status\" = 'Active' AND \"VerifiedAt\" IS NOT NULL AND \"ActivateAfter\" IS NOT NULL AND \"ActivatedAt\" IS NOT NULL AND \"DeletedAt\" IS NULL)\nOR (\"Status\" = 'Deleted' AND \"DeletedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_beneficiaries_timestamp_order",
                schema: "beneficiaries",
                table: "beneficiaries",
                sql: "\"CreatedAt\" <= \"UpdatedAt\"\nAND (\"VerifiedAt\" IS NULL OR \"VerifiedAt\" >= \"CreatedAt\")\nAND (\"ActivateAfter\" IS NULL OR (\"VerifiedAt\" IS NOT NULL AND \"ActivateAfter\" >= \"VerifiedAt\"))\nAND (\"ActivatedAt\" IS NULL OR (\"VerifiedAt\" IS NOT NULL AND \"ActivatedAt\" >= \"VerifiedAt\"))\nAND (\"DeletedAt\" IS NULL OR \"DeletedAt\" >= \"CreatedAt\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_beneficiaries_state_timestamps",
                schema: "beneficiaries",
                table: "beneficiaries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_beneficiaries_timestamp_order",
                schema: "beneficiaries",
                table: "beneficiaries");
        }
    }
}
