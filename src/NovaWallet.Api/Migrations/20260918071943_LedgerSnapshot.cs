using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Api.Migrations
{
    /// <inheritdoc />
    public partial class LedgerSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LedgerSnapshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletBalanceKobo = table.Column<long>(type: "bigint", nullable: false),
                    LedgerBalanceKobo = table.Column<long>(type: "bigint", nullable: false),
                    DiscrepancyKobo = table.Column<long>(type: "bigint", nullable: false),
                    IsBalanced = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerSnapshot", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerSnapshot_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerSnapshot_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSnapshot_AccountId",
                table: "LedgerSnapshot",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSnapshot_IsBalanced_Partial",
                table: "LedgerSnapshot",
                column: "CreatedAt",
                descending: new bool[0],
                filter: "\"IsBalanced\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSnapshot_WalletId_CreatedAt",
                table: "LedgerSnapshot",
                columns: new[] { "WalletId", "CreatedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LedgerSnapshot");
        }
    }
}
