using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelOps.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddReportGenerationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GeneratedAtUtc",
                table: "Reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HtmlStorageKey",
                table: "Reports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PdfStorageKey",
                table: "Reports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GeneratedAtUtc",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "HtmlStorageKey",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "PdfStorageKey",
                table: "Reports");
        }
    }
}
