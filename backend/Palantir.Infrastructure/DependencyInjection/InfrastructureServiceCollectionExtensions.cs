using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;
using Palantir.Application.Ai;
using Palantir.Application.Audit;
using Palantir.Application.Auth;
using Palantir.Application.Azure;
using Palantir.Application.Connectors;
using Palantir.Application.Knowledge;
using Palantir.Application.Overview;
using Palantir.Infrastructure.Ai;
using Palantir.Infrastructure.Connectors;
using Palantir.Infrastructure.Knowledge;
using Palantir.Infrastructure.Overview;
using Palantir.Infrastructure.Persistence;
using Palantir.Infrastructure.Services;

namespace Palantir.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddPalantirInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connectionString = configuration.GetConnectionString("Palantir")
            ?? "Data Source=palantir.dev.db";

        services.AddDbContext<PalantirDbContext>(options =>
        {
            if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                options.UseSqlServer(connectionString);
            }
            else
            {
                options.UseSqlite(connectionString);
            }
        });

        services.Configure<MicrosoftGraphOptions>(
            configuration.GetSection(MicrosoftGraphOptions.SectionName));
        services.Configure<MaintainXOptions>(
            configuration.GetSection(MaintainXOptions.SectionName));
        services.Configure<EZRentOutOptions>(
            configuration.GetSection(EZRentOutOptions.SectionName));
        services.Configure<MondayOptions>(
            configuration.GetSection(MondayOptions.SectionName));
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.Configure<OpsSnapshotOptions>(configuration.GetSection(OpsSnapshotOptions.SectionName));
        services.Configure<PilotJwtOptions>(configuration.GetSection(PilotJwtOptions.SectionName));
        services.Configure<AzureOptions>(configuration.GetSection(AzureOptions.SectionName));

        services.AddMemoryCache();
        services.AddHttpClient("microsoft-graph");
        services.AddHttpClient("maintainx");
        services.AddHttpClient("ezrentout", client =>
        {
            // Checked-out filter can span many pages; allow parallel pulls to finish.
            client.Timeout = TimeSpan.FromMinutes(2);
        });
        services.AddHttpClient("monday");
        services.AddHttpClient("palantir-ai", client =>
        {
            // Local Ollama can be slow on first token / cold start.
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddScoped<IPalantirDbContext>(sp => sp.GetRequiredService<PalantirDbContext>());
        services.AddScoped<IAuditEventWriter, AuditEventWriter>();
        services.AddSingleton<IConnectorCredentialStore, DataProtectionCredentialStore>();
        services.AddScoped<IMicrosoftGraphConnectorService, MicrosoftGraphConnectorService>();
        services.AddScoped<IMaintainXConnector, MaintainXConnector>();
        services.AddScoped<IEZRentOutConnector, EZRentOutConnector>();
        services.AddScoped<IMondayConnector, MondayConnector>();
        services.AddScoped<IOpsConnectorHealthService, OpsConnectorHealthService>();
        services.AddSingleton<IAccountingConnector, UnconfiguredAccountingConnector>();
        services.AddScoped<IAiCompletionClient, OpenAiCompatibleCompletionClient>();
        services.AddScoped<IOpsSnapshotStore, OpsSnapshotStore>();
        services.AddSingleton<IBlobKnowledgeStore>(sp =>
        {
            var azure = sp.GetRequiredService<IOptions<AzureOptions>>().Value;
            return azure.Storage.IsConfigured
                ? ActivatorUtilities.CreateInstance<AzureBlobKnowledgeStore>(sp)
                : new DisabledBlobKnowledgeStore();
        });
        services.AddSingleton<IKnowledgeIndexQueue, KnowledgeIndexQueue>();
        services.AddHostedService<KnowledgeIndexBackgroundService>();
        services.AddHostedService<OpsSnapshotRefreshBackgroundService>();

        return services;
    }
}
