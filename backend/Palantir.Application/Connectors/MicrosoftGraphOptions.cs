namespace Palantir.Application.Connectors;

public sealed class MicrosoftGraphOptions
{
    public const string SectionName = "Connectors:MicrosoftGraph";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    /// <summary>Home tenant where the app is registered.</summary>
    public string TenantId { get; set; } = string.Empty;
    /// <summary>Login authority tenant segment: use "common" for Outlook.com + work accounts.</summary>
    public string AuthorityTenant { get; set; } = "common";
    public string RedirectUri { get; set; } = "http://localhost:5251/oauth/microsoft/callback";
    public string FrontendSuccessUri { get; set; } = "http://localhost:5173/?outlook=connected";
    public string FrontendErrorUri { get; set; } = "http://localhost:5173/?outlook=error";
    public string[] Scopes { get; set; } =
    [
        "openid",
        "profile",
        "email",
        "offline_access",
        "User.Read",
        "Mail.Read",
        "Mail.Send"
    ];
    public string ExpectedPilotMailbox { get; set; } = string.Empty;
}
