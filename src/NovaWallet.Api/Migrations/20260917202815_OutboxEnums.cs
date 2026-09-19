using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Api.Migrations
{
    /// <inheritdoc />
    public partial class OutboxEnums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsProcessed",
                table: "ExternalCreditRequests");

            migrationBuilder.AddColumn<DateTime>(
                name: "DateModified",
                table: "WalletTransfers",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "WalletTransfers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Completed");

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedDate",
                table: "ExternalCreditRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateModified",
                table: "ExternalCreditRequests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "ExternalCreditRequests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<DateTime>(
                name: "DateProcessed",
                table: "DepositOutbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NumberOfRetries",
                table: "DepositOutbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DateModified",
                table: "WalletTransfers");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "WalletTransfers");

            migrationBuilder.DropColumn(
                name: "CompletedDate",
                table: "ExternalCreditRequests");

            migrationBuilder.DropColumn(
                name: "DateModified",
                table: "ExternalCreditRequests");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ExternalCreditRequests");

            migrationBuilder.DropColumn(
                name: "DateProcessed",
                table: "DepositOutbox");

            migrationBuilder.DropColumn(
                name: "NumberOfRetries",
                table: "DepositOutbox");

            migrationBuilder.AddColumn<bool>(
                name: "IsProcessed",
                table: "ExternalCreditRequests",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
