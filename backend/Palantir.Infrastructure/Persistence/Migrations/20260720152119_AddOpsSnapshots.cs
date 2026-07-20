using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palantir.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOpsSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpsSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FocusKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpsSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpsSnapshots_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpsSnapshots_OrganizationId_FocusKey",
                table: "OpsSnapshots",
                columns: new[] { "OrganizationId", "FocusKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpsSnapshots");
        }
    }
}
