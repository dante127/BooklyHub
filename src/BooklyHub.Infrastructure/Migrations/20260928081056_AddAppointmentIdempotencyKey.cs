using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BooklyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "Appointments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_Tenant_IdempotencyKey",
                table: "Appointments",
                columns: new[] { "TenantId", "IdempotencyKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Appointments_Tenant_IdempotencyKey",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "Appointments");
        }
    }
}
