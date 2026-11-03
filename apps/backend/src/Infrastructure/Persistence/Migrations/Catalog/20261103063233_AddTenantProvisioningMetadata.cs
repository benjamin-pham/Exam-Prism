using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamPrism.Infrastructure.Persistence.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class AddTenantProvisioningMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedOwnerPassword",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerEmail",
                table: "Tenants",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedOwnerPassword",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OwnerEmail",
                table: "Tenants");
        }
    }
}
