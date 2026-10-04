using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BooklyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentLocationOccupancyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Appointments_Tenant_Location_EndAt",
                table: "Appointments",
                columns: new[] { "TenantId", "LocationId", "EndAtUtc" })
                .Annotation("SqlServer:Include", new[] { "StartAtUtc", "StaffId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Appointments_Tenant_Location_EndAt",
                table: "Appointments");
        }
    }
}
