using System.Text.Json;
using Palantir.Application.Approvals;
using Palantir.Application.Conversations;
using Palantir.Application.Outbound;
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
        services.AddScoped<IOutboundEmailService, OutboundEmailService>();
        return services;
    }
}
