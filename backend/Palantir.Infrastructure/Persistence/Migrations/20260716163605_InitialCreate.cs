using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palantir.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Connectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: true),
                    DeviceId = table.Column<Guid>(nullable: true),
                    ConnectorType = table.Column<string>(maxLength: 100, nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false),
                    Status = table.Column<string>(maxLength: 50, nullable: false),
                    CapabilitiesJson = table.Column<string>(nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connectors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Connectors_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 300, nullable: false),
                    MetadataJson = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    ProjectId = table.Column<Guid>(nullable: true),
                    ConversationId = table.Column<Guid>(nullable: true),
                    CreatedByUserId = table.Column<Guid>(nullable: false),
                    AssignedToUserId = table.Column<Guid>(nullable: true),
                    AssignedToTeamId = table.Column<Guid>(nullable: true),
                    Title = table.Column<string>(maxLength: 500, nullable: false),
                    Description = table.Column<string>(nullable: true),
                    DueAt = table.Column<DateTimeOffset>(nullable: true),
                    Status = table.Column<string>(maxLength: 50, nullable: false),
                    Priority = table.Column<string>(maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Teams_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    DisplayName = table.Column<string>(maxLength: 200, nullable: false),
                    Email = table.Column<string>(maxLength: 320, nullable: false),
                    IsActive = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    CustomerId = table.Column<Guid>(nullable: true),
                    DisplayName = table.Column<string>(maxLength: 300, nullable: false),
                    Email = table.Column<string>(maxLength: 320, nullable: true),
                    Phone = table.Column<string>(maxLength: 50, nullable: true),
                    MetadataJson = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contacts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    ActorUserId = table.Column<Guid>(nullable: true),
                    EventType = table.Column<string>(maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(maxLength: 100, nullable: true),
                    EntityId = table.Column<Guid>(nullable: true),
                    DetailsJson = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    Provider = table.Column<string>(maxLength: 64, nullable: false),
                    ProviderTenantId = table.Column<string>(nullable: true),
                    ProviderAccountId = table.Column<string>(maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(nullable: true),
                    PrimaryAddress = table.Column<string>(nullable: true),
                    ConnectionStatus = table.Column<string>(maxLength: 64, nullable: false),
                    GrantedScopesJson = table.Column<string>(nullable: true),
                    LastSuccessfulSyncAt = table.Column<DateTimeOffset>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedAccounts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    DeviceName = table.Column<string>(maxLength: 200, nullable: false),
                    Platform = table.Column<string>(maxLength: 50, nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(nullable: true),
                    PushToken = table.Column<string>(nullable: true),
                    IsActive = table.Column<bool>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "ExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    Provider = table.Column<string>(maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(maxLength: 512, nullable: false),
                    ProviderTenantId = table.Column<string>(maxLength: 128, nullable: true),
                    ProviderSubjectId = table.Column<string>(maxLength: 256, nullable: false),
                    Email = table.Column<string>(maxLength: 320, nullable: true),
                    IsLoginEnabled = table.Column<bool>(nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalIdentities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    CustomerId = table.Column<Guid>(nullable: true),
                    Name = table.Column<string>(maxLength: 300, nullable: false),
                    Status = table.Column<string>(maxLength: 50, nullable: false),
                    OwnerUserId = table.Column<Guid>(nullable: true),
                    MetadataJson = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Projects_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Projects_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "OAuthGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ConnectedAccountId = table.Column<Guid>(nullable: false),
                    CredentialReference = table.Column<string>(maxLength: 512, nullable: false),
                    TokenVersion = table.Column<int>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthGrants_ConnectedAccounts_ConnectedAccountId",
                        column: x => x.ConnectedAccountId,
                        principalTable: "ConnectedAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    CustomerId = table.Column<Guid>(nullable: true),
                    ProjectId = table.Column<Guid>(nullable: true),
                    Subject = table.Column<string>(maxLength: 500, nullable: true),
                    Channel = table.Column<string>(maxLength: 50, nullable: false),
                    Status = table.Column<string>(maxLength: 50, nullable: false),
                    AssignedUserId = table.Column<Guid>(nullable: true),
                    AssignedTeamId = table.Column<Guid>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Conversations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Conversations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Conversations_Teams_AssignedTeamId",
                        column: x => x.AssignedTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Conversations_Users_AssignedUserId",
                        column: x => x.AssignedUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Drafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ConversationId = table.Column<Guid>(nullable: false),
                    CreatedByUserId = table.Column<Guid>(nullable: true),
                    CreatedByAi = table.Column<bool>(nullable: false),
                    Body = table.Column<string>(nullable: false),
                    Revision = table.Column<int>(nullable: false),
                    Status = table.Column<string>(maxLength: 50, nullable: false),
                    MetadataJson = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Drafts_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Drafts_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ConversationId = table.Column<Guid>(nullable: false),
                    Direction = table.Column<string>(maxLength: 20, nullable: false),
                    SenderUserId = table.Column<Guid>(nullable: true),
                    ContactId = table.Column<Guid>(nullable: true),
                    Body = table.Column<string>(nullable: true),
                    Summary = table.Column<string>(nullable: true),
                    ProviderMessageId = table.Column<string>(maxLength: 300, nullable: true),
                    ProviderMetadataJson = table.Column<string>(nullable: true),
                    IsInternalNote = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Messages_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Messages_Users_SenderUserId",
                        column: x => x.SenderUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    DraftId = table.Column<Guid>(nullable: true),
                    RequestedForUserId = table.Column<Guid>(nullable: false),
                    Status = table.Column<string>(maxLength: 50, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(nullable: true),
                    CompletedByUserId = table.Column<Guid>(nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: true),
                    DraftRevision = table.Column<int>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_Drafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "Drafts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_Users_CompletedByUserId",
                        column: x => x.CompletedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_Users_RequestedForUserId",
                        column: x => x.RequestedForUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OrganizationId = table.Column<Guid>(nullable: false),
                    ActionType = table.Column<string>(maxLength: 100, nullable: false),
                    Status = table.Column<string>(maxLength: 50, nullable: false),
                    RequestedByUserId = table.Column<Guid>(nullable: true),
                    ApprovalRequestId = table.Column<Guid>(nullable: true),
                    ClaimedByConnectorId = table.Column<Guid>(nullable: true),
                    IdempotencyKey = table.Column<string>(maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(nullable: false),
                    ResultJson = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Actions_ApprovalRequests_ApprovalRequestId",
                        column: x => x.ApprovalRequestId,
                        principalTable: "ApprovalRequests",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Actions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Actions_Users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Actions_ApprovalRequestId",
                table: "Actions",
                column: "ApprovalRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Actions_IdempotencyKey",
                table: "Actions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Actions_OrganizationId",
                table: "Actions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Actions_RequestedByUserId",
                table: "Actions",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_CompletedByUserId",
                table: "ApprovalRequests",
                column: "CompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_DraftId",
                table: "ApprovalRequests",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_RequestedForUserId",
                table: "ApprovalRequests",
                column: "RequestedForUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorUserId",
                table: "AuditEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OrganizationId",
                table: "AuditEvents",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedAccounts_UserId",
                table: "ConnectedAccounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Connectors_OrganizationId",
                table: "Connectors",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_CustomerId",
                table: "Contacts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AssignedTeamId",
                table: "Conversations",
                column: "AssignedTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AssignedUserId",
                table: "Conversations",
                column: "AssignedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_CustomerId",
                table: "Conversations",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_OrganizationId",
                table: "Conversations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ProjectId",
                table: "Conversations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_OrganizationId",
                table: "Customers",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_UserId",
                table: "Devices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_ConversationId",
                table: "Drafts",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_CreatedByUserId",
                table: "Drafts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_Issuer_ProviderSubjectId",
                table: "ExternalIdentities",
                columns: new[] { "Issuer", "ProviderSubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_UserId",
                table: "ExternalIdentities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ContactId",
                table: "Messages",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SenderUserId",
                table: "Messages",
                column: "SenderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthGrants_ConnectedAccountId",
                table: "OAuthGrants",
                column: "ConnectedAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CustomerId",
                table: "Projects",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OrganizationId",
                table: "Projects",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OwnerUserId",
                table: "Projects",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_OrganizationId",
                table: "Tasks",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_OrganizationId",
                table: "Teams",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_OrganizationId_Email",
                table: "Users",
                columns: new[] { "OrganizationId", "Email" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Actions");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "Connectors");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "ExternalIdentities");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "OAuthGrants");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "ConnectedAccounts");

            migrationBuilder.DropTable(
                name: "Drafts");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Organizations");
        }
    }
}
