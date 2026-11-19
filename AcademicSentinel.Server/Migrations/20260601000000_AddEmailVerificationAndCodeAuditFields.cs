using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcademicSentinel.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerificationAndCodeAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Email-verification (registration) fields.
            migrationBuilder.AddColumn<bool>(
                name: "IsEmailVerified",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailVerificationCodeHash",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerificationExpiresAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmailVerificationAttempts",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastVerificationCodeSentAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            // Forgot-password audit fields.
            migrationBuilder.AddColumn<int>(
                name: "PasswordResetAttempts",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastResetCodeSentAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill: every pre-existing account is grandfathered as
            // verified so existing students/instructors aren't locked
            // out by the new login gate. Only newly-registered accounts
            // start unverified.
            migrationBuilder.Sql("UPDATE \"Users\" SET \"IsEmailVerified\" = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IsEmailVerified",           table: "Users");
            migrationBuilder.DropColumn(name: "EmailVerificationCodeHash", table: "Users");
            migrationBuilder.DropColumn(name: "EmailVerificationExpiresAt",table: "Users");
            migrationBuilder.DropColumn(name: "EmailVerificationAttempts", table: "Users");
            migrationBuilder.DropColumn(name: "LastVerificationCodeSentAt",table: "Users");
            migrationBuilder.DropColumn(name: "PasswordResetAttempts",     table: "Users");
            migrationBuilder.DropColumn(name: "LastResetCodeSentAt",       table: "Users");
        }
    }
}
