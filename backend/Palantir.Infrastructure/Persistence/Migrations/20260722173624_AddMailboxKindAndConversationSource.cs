using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palantir.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxKindAndConversationSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceConnectedAccountId",
                table: "Conversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceMailboxKind",
                table: "Conversations",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MailboxKind",
                table: "ConnectedAccounts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Work");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_SourceConnectedAccountId",
                table: "Conversations",
                column: "SourceConnectedAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_ConnectedAccounts_SourceConnectedAccountId",
                table: "Conversations",
                column: "SourceConnectedAccountId",
                principalTable: "ConnectedAccounts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_ConnectedAccounts_SourceConnectedAccountId",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_SourceConnectedAccountId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "SourceConnectedAccountId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "SourceMailboxKind",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "MailboxKind",
                table: "ConnectedAccounts");
        }
    }
}
