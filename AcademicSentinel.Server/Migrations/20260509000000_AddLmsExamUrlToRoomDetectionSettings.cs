using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicSentinel.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddLmsExamUrlToRoomDetectionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // LMS exam URL — anchored focus detection (REQUIRED).
            // NOT NULL with empty-string default so existing rows backfill cleanly.
            // The application layer enforces non-empty validation; the DB column
            // is NOT NULL purely to guarantee shape integrity.
            migrationBuilder.AddColumn<string>(
                name: "LmsExamUrl",
                table: "RoomDetectionSettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LmsExamUrl",
                table: "RoomDetectionSettings");
        }
    }
}
