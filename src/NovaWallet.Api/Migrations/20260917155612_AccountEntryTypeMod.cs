using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Api.Migrations
{
    /// <inheritdoc />
    public partial class AccountEntryTypeMod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CHK_AccountEntries_ExclusiveSign",
                table: "AccountEntries");

            migrationBuilder.DropColumn(
                name: "CreditAmountKobo",
                table: "AccountEntries");

            migrationBuilder.RenameColumn(
                name: "DebitAmountKobo",
                table: "AccountEntries",
                newName: "AmountKobo");

            migrationBuilder.AddColumn<string>(
                name: "EntryType",
                table: "AccountEntries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TransParticulars",
                table: "AccountEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "WalletTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountKobo = table.Column<long>(type: "bigint", nullable: false),
                    Narration = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaymentReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletTransfers", x => x.Id);
                    table.CheckConstraint("CHK_WalletTransfers_AmountPositive", "\"AmountKobo\" > 0");
                    table.ForeignKey(
                        name: "FK_WalletTransfers_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletTransfers_Wallets_DestinationWalletId",
                        column: x => x.DestinationWalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletTransfers_Wallets_SourceWalletId",
                        column: x => x.SourceWalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CHK_AccountEntries_PositiveAmount",
                table: "AccountEntries",
                sql: "\"AmountKobo\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransfers_DestinationWalletId_TransactionDate",
                table: "WalletTransfers",
                columns: new[] { "DestinationWalletId", "TransactionDate" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransfers_JournalEntryId",
                table: "WalletTransfers",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransfers_PaymentReference",
                table: "WalletTransfers",
                column: "PaymentReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransfers_SourceWalletId_TransactionDate",
                table: "WalletTransfers",
                columns: new[] { "SourceWalletId", "TransactionDate" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CHK_AccountEntries_PositiveAmount",
                table: "AccountEntries");

            migrationBuilder.DropColumn(
                name: "EntryType",
                table: "AccountEntries");

            migrationBuilder.DropColumn(
                name: "TransParticulars",
                table: "AccountEntries");

            migrationBuilder.RenameColumn(
                name: "AmountKobo",
                table: "AccountEntries",
                newName: "DebitAmountKobo");

            migrationBuilder.AddColumn<long>(
                name: "CreditAmountKobo",
                table: "AccountEntries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "CHK_AccountEntries_ExclusiveSign",
                table: "AccountEntries",
                sql: "(\"DebitAmountKobo\" > 0 AND \"CreditAmountKobo\" = 0)\r\nOR\r\n(\"DebitAmountKobo\" = 0 AND \"CreditAmountKobo\" > 0)");
        }
    }
}
