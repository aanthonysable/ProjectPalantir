namespace Palantir.Application.Auth;

public sealed class EntraExternalIdOptions
{
    public const string SectionName = "Authentication:EntraExternalId";

    /// <summary>When false or Authority empty, Microsoft sign-in is hidden.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// OIDC authority, e.g. https://{tenant}.ciamlogin.com/{tenant-id}/v2.0
    /// or https://login.microsoftonline.com/{tenant-id}/v2.0 for a workforce tenant during early pilot.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>SPA / public client application (client) ID.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>API audience — usually api://{api-app-id} or the API app client ID.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Optional tenant id for docs / UI.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Scopes the SPA requests, e.g. api://.../access_as_user openid profile email</summary>
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    public string ProviderName { get; set; } = "entra-external-id";
}

public sealed record AuthProvidersDto(
    bool LocalPasswordEnabled,
    EntraProviderDto? EntraExternalId);

public sealed record EntraProviderDto(
    bool Enabled,
    string Authority,
    string ClientId,
    string Audience,
    string TenantId,
    IReadOnlyList<string> Scopes);

public interface IEntraExternalIdAuthService
{
    bool IsConfigured { get; }
    AuthProvidersDto GetProviders();
    Task<PilotLoginResult> ExchangeAsync(string entraAccessOrIdToken, CancellationToken cancellationToken = default);
}
