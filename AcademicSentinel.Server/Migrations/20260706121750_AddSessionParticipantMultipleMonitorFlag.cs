using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicSentinel.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionParticipantMultipleMonitorFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasMultipleMonitors",
                table: "SessionParticipants",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasMultipleMonitors",
                table: "SessionParticipants");
        }
    }
}
