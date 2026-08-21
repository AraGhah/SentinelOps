using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelOps.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessedWorkerEventOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Completed",
                table: "ProcessedWorkerEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PendingOutboxJson",
                table: "ProcessedWorkerEvents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Completed",
                table: "ProcessedWorkerEvents");

            migrationBuilder.DropColumn(
                name: "PendingOutboxJson",
                table: "ProcessedWorkerEvents");
        }
    }
}
