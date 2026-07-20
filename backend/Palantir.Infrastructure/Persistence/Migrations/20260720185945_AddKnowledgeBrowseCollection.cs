using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palantir.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeBrowseCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Collection",
                table: "KnowledgeDocuments",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FolderPath",
                table: "KnowledgeDocuments",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_OrganizationId_Collection_FolderPath",
                table: "KnowledgeDocuments",
                columns: new[] { "OrganizationId", "Collection", "FolderPath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KnowledgeDocuments_OrganizationId_Collection_FolderPath",
                table: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "Collection",
                table: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "FolderPath",
                table: "KnowledgeDocuments");
        }
    }
}
