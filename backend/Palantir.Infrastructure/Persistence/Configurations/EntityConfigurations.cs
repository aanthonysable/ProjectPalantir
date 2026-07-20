using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palantir.Domain.Entities;

namespace Palantir.Infrastructure.Persistence.Configurations;

public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.HasIndex(x => new { x.OrganizationId, x.Email });
        builder.HasOne(x => x.Organization).WithMany(x => x.Users).HasForeignKey(x => x.OrganizationId);
    }
}

public sealed class ExternalIdentityConfiguration : IEntityTypeConfiguration<ExternalIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentity> builder)
    {
        builder.ToTable("ExternalIdentities");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Issuer).HasMaxLength(512).IsRequired();
        builder.Property(x => x.ProviderSubjectId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ProviderTenantId).HasMaxLength(128);
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.HasIndex(x => new { x.Issuer, x.ProviderSubjectId }).IsUnique();
        builder.HasOne(x => x.User).WithMany(x => x.ExternalIdentities).HasForeignKey(x => x.UserId);
    }
}

public sealed class ConnectedAccountConfiguration : IEntityTypeConfiguration<ConnectedAccount>
{
    public void Configure(EntityTypeBuilder<ConnectedAccount> builder)
    {
        builder.ToTable("ConnectedAccounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProviderAccountId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ConnectionStatus).HasConversion<string>().HasMaxLength(64);
        builder.HasOne(x => x.User).WithMany(x => x.ConnectedAccounts).HasForeignKey(x => x.UserId);
    }
}

public sealed class OAuthGrantConfiguration : IEntityTypeConfiguration<OAuthGrant>
{
    public void Configure(EntityTypeBuilder<OAuthGrant> builder)
    {
        builder.ToTable("OAuthGrants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CredentialReference).HasMaxLength(512).IsRequired();
        builder.HasOne(x => x.ConnectedAccount).WithMany(x => x.OAuthGrants).HasForeignKey(x => x.ConnectedAccountId);
    }
}

public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("Teams");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
    }
}

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("Devices");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DeviceName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Platform).HasMaxLength(50).IsRequired();
        builder.HasOne(x => x.User).WithMany(x => x.Devices).HasForeignKey(x => x.UserId);
    }
}

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(300).IsRequired();
    }
}

public sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("Contacts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DisplayName).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.Property(x => x.Phone).HasMaxLength(50);
    }
}

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Subject).HasMaxLength(500);
        builder.Property(x => x.Channel).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50);
        builder.HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.AssignedTeam).WithMany().HasForeignKey(x => x.AssignedTeamId).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Direction).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ProviderMessageId).HasMaxLength(300);
        builder.HasOne(x => x.Conversation).WithMany(x => x.Messages).HasForeignKey(x => x.ConversationId);
    }
}

public sealed class DraftConfiguration : IEntityTypeConfiguration<Draft>
{
    public void Configure(EntityTypeBuilder<Draft> builder)
    {
        builder.ToTable("Drafts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Body).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.HasOne(x => x.Conversation).WithMany(x => x.Drafts).HasForeignKey(x => x.ConversationId);
    }
}

public sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        builder.ToTable("ApprovalRequests");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50);
        builder.HasOne(x => x.Draft).WithMany(x => x.ApprovalRequests).HasForeignKey(x => x.DraftId);
        builder.HasOne(x => x.RequestedForUser).WithMany().HasForeignKey(x => x.RequestedForUserId);
        builder.HasOne(x => x.CompletedByUser).WithMany().HasForeignKey(x => x.CompletedByUserId);
    }
}

public sealed class WorkflowActionConfiguration : IEntityTypeConfiguration<WorkflowAction>
{
    public void Configure(EntityTypeBuilder<WorkflowAction> builder)
    {
        builder.ToTable("Actions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ActionType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => x.IdempotencyKey).IsUnique();
        builder.HasOne(x => x.ApprovalRequest).WithMany().HasForeignKey(x => x.ApprovalRequestId);
    }
}

public sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("Tasks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Priority).HasMaxLength(50).IsRequired();
    }
}

public sealed class ConnectorConfiguration : IEntityTypeConfiguration<Connector>
{
    public void Configure(EntityTypeBuilder<Connector> builder)
    {
        builder.ToTable("Connectors");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ConnectorType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.CapabilitiesJson).IsRequired();
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityType).HasMaxLength(100);
        builder.HasOne(x => x.ActorUser).WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class LocalPilotCredentialConfiguration : IEntityTypeConfiguration<LocalPilotCredential>
{
    public void Configure(EntityTypeBuilder<LocalPilotCredential> builder)
    {
        builder.ToTable("LocalPilotCredentials");
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.PasswordHash).IsRequired();
        builder.HasOne(x => x.User).WithOne().HasForeignKey<LocalPilotCredential>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        builder.ToTable("KnowledgeDocuments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(300).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(260).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
        builder.Property(x => x.BlobPath).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(40).IsRequired();
        builder.Property(x => x.IndexError).HasMaxLength(500);
        builder.Property(x => x.Tags).HasMaxLength(800);
        builder.Property(x => x.Collection).HasMaxLength(120).IsRequired();
        builder.Property(x => x.FolderPath).HasMaxLength(260);
        builder.Property(x => x.ContentHash).HasMaxLength(64);
        builder.HasIndex(x => new { x.OrganizationId, x.ContentHash });
        builder.HasIndex(x => new { x.OrganizationId, x.Collection, x.FolderPath });
        builder.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        builder.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.UploadedByUser).WithMany().HasForeignKey(x => x.UploadedByUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => x.DuplicateOfDocumentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunk> builder)
    {
        builder.ToTable("KnowledgeChunks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Text).IsRequired();
        builder.HasIndex(x => new { x.DocumentId, x.Ordinal }).IsUnique();
        builder.HasOne(x => x.Document).WithMany(x => x.Chunks).HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AskSessionConfiguration : IEntityTypeConfiguration<AskSession>
{
    public void Configure(EntityTypeBuilder<AskSession> builder)
    {
        builder.ToTable("AskSessions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => new { x.OrganizationId, x.UserId, x.UpdatedAt });
        builder.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class AskMessageConfiguration : IEntityTypeConfiguration<AskMessage>
{
    public void Configure(EntityTypeBuilder<AskMessage> builder)
    {
        builder.ToTable("AskMessages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Role).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Content).IsRequired();
        builder.HasIndex(x => new { x.SessionId, x.Ordinal }).IsUnique();
        builder.HasOne(x => x.Session).WithMany(x => x.Messages).HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OpsSnapshotConfiguration : IEntityTypeConfiguration<OpsSnapshot>
{
    public void Configure(EntityTypeBuilder<OpsSnapshot> builder)
    {
        builder.ToTable("OpsSnapshots");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FocusKey).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Error).HasMaxLength(500);
        builder.Property(x => x.SnapshotJson).IsRequired();
        builder.HasIndex(x => new { x.OrganizationId, x.FocusKey }).IsUnique();
        builder.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AskAttachmentConfiguration : IEntityTypeConfiguration<AskAttachment>
{
    public void Configure(EntityTypeBuilder<AskAttachment> builder)
    {
        builder.ToTable("AskAttachments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FileName).HasMaxLength(260).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
        builder.Property(x => x.BlobPath).HasMaxLength(500);
        builder.Property(x => x.ExtractStatus).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ExtractedText).IsRequired();
        builder.HasIndex(x => new { x.OrganizationId, x.UserId, x.CreatedAt });
        builder.HasIndex(x => x.SessionId);
        builder.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
