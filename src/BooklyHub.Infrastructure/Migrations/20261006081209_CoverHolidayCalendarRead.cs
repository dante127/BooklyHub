using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BooklyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CoverHolidayCalendarRead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Holidays_TenantId_Date",
                table: "Holidays");

            migrationBuilder.CreateIndex(
                name: "IX_Holidays_TenantId_Date",
                table: "Holidays",
                columns: new[] { "TenantId", "Date" })
                .Annotation("SqlServer:Include", new[] { "LocationId", "RecurringAnnually" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Holidays_TenantId_Date",
                table: "Holidays");

            migrationBuilder.CreateIndex(
                name: "IX_Holidays_TenantId_Date",
                table: "Holidays",
                columns: new[] { "TenantId", "Date" });
        }
    }
}
