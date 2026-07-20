using Palantir.Application.Ai;
using Palantir.Application.Approvals;
using Palantir.Application.Ask;
using Palantir.Application.Conversations;
using Palantir.Application.Knowledge;
using Palantir.Application.Ops;
using Palantir.Application.Outbound;
using Palantir.Application.Overview;
using Palantir.Application.Tasks;
using Palantir.Application.Connectors;
using Microsoft.Extensions.DependencyInjection;

namespace Palantir.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddPalantirApplication(this IServiceCollection services)
    {
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<ITaskService, TaskService>();
        services.AddScoped<IOutlookInboxSyncService, OutlookInboxSyncService>();
        services.AddScoped<IWhatsAppIngestService, WhatsAppIngestService>();
        services.AddScoped<IWhatsAppOpsWatchService, WhatsAppOpsWatchService>();
        services.AddScoped<IOutboundEmailService, OutboundEmailService>();
        services.AddScoped<IOpsWriteBackService, OpsWriteBackService>();
        services.AddScoped<IAiAssistantService, AiAssistantService>();
        services.AddScoped<IOverviewService, OverviewService>();
        services.AddScoped<IAskHistoryService, AskHistoryService>();
        services.AddScoped<IAskAttachmentService, AskAttachmentService>();
        services.AddScoped<IKnowledgeService, KnowledgeService>();
        services.AddScoped<IKnowledgeCaptureService, KnowledgeCaptureService>();
        return services;
    }
}
