using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicSentinel.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDeleteToExamSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Soft-delete column for Past Sessions Trash. Null = live;
            // non-null = trashed, retained until ArchiveCleanupService
            // hard-deletes it after the configured retention window.
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ExamSessions",
                type: "timestamp with time zone",
                nullable: true);

            // Index makes the cleanup worker's "where DeletedAt < cutoff"
            // sweep cheap, and likewise the history endpoints' "where
            // DeletedAt is null" filter.
            migrationBuilder.CreateIndex(
                name: "IX_ExamSessions_DeletedAt",
                table: "ExamSessions",
                column: "DeletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExamSessions_DeletedAt",
                table: "ExamSessions");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ExamSessions");
        }
    }
}
