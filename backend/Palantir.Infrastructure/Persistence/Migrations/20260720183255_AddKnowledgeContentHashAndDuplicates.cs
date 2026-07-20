using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palantir.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeContentHashAndDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "KnowledgeDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DuplicateOfDocumentId",
                table: "KnowledgeDocuments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_DuplicateOfDocumentId",
                table: "KnowledgeDocuments",
                column: "DuplicateOfDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_OrganizationId_ContentHash",
                table: "KnowledgeDocuments",
                columns: new[] { "OrganizationId", "ContentHash" });

            migrationBuilder.AddForeignKey(
                name: "FK_KnowledgeDocuments_KnowledgeDocuments_DuplicateOfDocumentId",
                table: "KnowledgeDocuments",
                column: "DuplicateOfDocumentId",
                principalTable: "KnowledgeDocuments",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KnowledgeDocuments_KnowledgeDocuments_DuplicateOfDocumentId",
                table: "KnowledgeDocuments");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeDocuments_DuplicateOfDocumentId",
                table: "KnowledgeDocuments");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeDocuments_OrganizationId_ContentHash",
                table: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "DuplicateOfDocumentId",
                table: "KnowledgeDocuments");
        }
    }
}
