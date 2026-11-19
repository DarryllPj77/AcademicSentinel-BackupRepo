using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicSentinel.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAllowedAppsToRoomDetectionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Per-room allow-list of apps the student may alt-tab to
            // during the exam without triggering WINDOW_SWITCH or
            // PROCESS_DETECTED violations. Comma-separated tokens,
            // free-form text — see Models/RoomDetectionSettings.cs
            // and Services/SAC/BehavioralMonitoringService.cs for the
            // token format and runtime matching semantics. Nullable
            // because the column is opt-in; existing rows backfill
            // as NULL (feature disabled).
            migrationBuilder.AddColumn<string>(
                name: "AllowedAppsCsv",
                table: "RoomDetectionSettings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedAppsCsv",
                table: "RoomDetectionSettings");
        }
    }
}
