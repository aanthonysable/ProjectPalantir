using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Application.Connectors;
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

        services.AddMemoryCache();
        services.AddHttpClient("microsoft-graph");
        services.AddScoped<IPalantirDbContext>(sp => sp.GetRequiredService<PalantirDbContext>());
        services.AddScoped<IAuditEventWriter, AuditEventWriter>();
        services.AddSingleton<IConnectorCredentialStore, DataProtectionCredentialStore>();
        services.AddScoped<IMicrosoftGraphConnectorService, MicrosoftGraphConnectorService>();

        return services;
    }
}
