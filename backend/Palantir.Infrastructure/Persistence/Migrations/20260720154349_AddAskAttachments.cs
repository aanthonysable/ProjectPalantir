using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palantir.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAskAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AskAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ByteSize = table.Column<long>(type: "bigint", nullable: false),
                    BlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExtractedText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExtractStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    KnowledgeDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AskAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AskAttachments_AskSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AskSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AskAttachments_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AskAttachments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AskAttachments_OrganizationId_UserId_CreatedAt",
                table: "AskAttachments",
                columns: new[] { "OrganizationId", "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AskAttachments_SessionId",
                table: "AskAttachments",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AskAttachments_UserId",
                table: "AskAttachments",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AskAttachments");
        }
    }
}
