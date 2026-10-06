using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Customers.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminSearchKeysetIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_customers_CreatedAt_Id",
                schema: "customers",
                table: "customers",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_KycStatus_CreatedAt_Id",
                schema: "customers",
                table: "customers",
                columns: new[] { "KycStatus", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_Status_CreatedAt_Id",
                schema: "customers",
                table: "customers",
                columns: new[] { "Status", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_customers_CreatedAt_Id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_KycStatus_CreatedAt_Id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_Status_CreatedAt_Id",
                schema: "customers",
                table: "customers");
        }
    }
}
