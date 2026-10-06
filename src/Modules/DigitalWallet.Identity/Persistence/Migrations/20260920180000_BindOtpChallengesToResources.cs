using DigitalWallet.Identity.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Identity.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20260920180000_BindOtpChallengesToResources")]
public sealed class BindOtpChallengesToResources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "ResourceBinding", schema: "identity", table: "otp_challenges", type: "character varying(160)", maxLength: 160, nullable: true);
        migrationBuilder.CreateIndex(name: "IX_otp_challenges_UserId_Purpose_ResourceBinding_ExpiresAt", schema: "identity", table: "otp_challenges", columns: new[] { "UserId", "Purpose", "ResourceBinding", "ExpiresAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_otp_challenges_UserId_Purpose_ResourceBinding_ExpiresAt", schema: "identity", table: "otp_challenges");
        migrationBuilder.DropColumn(name: "ResourceBinding", schema: "identity", table: "otp_challenges");
    }
}
