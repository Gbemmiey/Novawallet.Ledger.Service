using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Api.Migrations
{
    /// <inheritdoc />
    public partial class WalletTransferFailedTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "JournalEntryId",
                table: "WalletTransfers",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "WalletTransfers",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "WalletTransfers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "WalletTransfers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestPayloadHash",
                table: "WalletTransfers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransfers_IdempotencyKey",
                table: "WalletTransfers",
                column: "IdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletTransfers_IdempotencyKey",
                table: "WalletTransfers");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "WalletTransfers");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "WalletTransfers");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "WalletTransfers");

            migrationBuilder.DropColumn(
                name: "RequestPayloadHash",
                table: "WalletTransfers");

            migrationBuilder.AlterColumn<Guid>(
                name: "JournalEntryId",
                table: "WalletTransfers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
