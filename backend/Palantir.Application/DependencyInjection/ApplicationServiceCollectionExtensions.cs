using Microsoft.Extensions.DependencyInjection;
using Palantir.Application.Approvals;
using Palantir.Application.Conversations;

namespace Palantir.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddPalantirApplication(this IServiceCollection services)
    {
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IApprovalService, ApprovalService>();
        return services;
    }
}
