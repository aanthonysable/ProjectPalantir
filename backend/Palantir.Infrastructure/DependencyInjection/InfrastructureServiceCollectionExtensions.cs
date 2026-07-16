using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Palantir.Application.Abstractions;
using Palantir.Application.Ai;
using Palantir.Application.Audit;
using Palantir.Application.Auth;
using Palantir.Application.Connectors;
using Palantir.Infrastructure.Ai;
using Palantir.Infrastructure.Connectors;
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
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.Configure<PilotJwtOptions>(configuration.GetSection(PilotJwtOptions.SectionName));

        services.AddMemoryCache();
        services.AddHttpClient("microsoft-graph");
        services.AddHttpClient("palantir-ai", client =>
        {
            // Local Ollama can be slow on first token / cold start.
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddScoped<IPalantirDbContext>(sp => sp.GetRequiredService<PalantirDbContext>());
        services.AddScoped<IAuditEventWriter, AuditEventWriter>();
        services.AddSingleton<IConnectorCredentialStore, DataProtectionCredentialStore>();
        services.AddScoped<IMicrosoftGraphConnectorService, MicrosoftGraphConnectorService>();
        services.AddScoped<IAiCompletionClient, OpenAiCompatibleCompletionClient>();

        return services;
    }
}
