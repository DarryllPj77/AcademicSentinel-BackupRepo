using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicSentinel.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddIsLoggedInFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExamSessions_DeletedAt",
                table: "ExamSessions");

            migrationBuilder.AddColumn<bool>(
                name: "IsLoggedIn",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsLoggedIn",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_ExamSessions_DeletedAt",
                table: "ExamSessions",
                column: "DeletedAt");
        }
    }
}
