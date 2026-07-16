namespace Palantir.Application.Auth;

public sealed class PilotJwtOptions
{
    public const string SectionName = "Authentication:PilotJwt";

    public string Issuer { get; set; } = "https://login.palantir.local/";
    public string Audience { get; set; } = "api://palantir";

    /// <summary>Symmetric signing key (dev). Prefer user-secrets / Key Vault in shared environments.</summary>
    public string SigningKey { get; set; } = "Palantir-Pilot-Dev-Signing-Key-Change-Me-32+";

    public int LifetimeHours { get; set; } = 12;
}

public sealed record PilotLoginRequest(string Email, string Password);

public sealed record PilotLoginResult(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    Guid OrganizationId,
    string DisplayName,
    string Email,
    string AuthMode);

public sealed record MeResult(
    Guid UserId,
    Guid OrganizationId,
    string DisplayName,
    string Email,
    string AuthMode);

public sealed record PilotRegisterRequest(
    string Email,
    string Password,
    string DisplayName,
    Guid? OrganizationId = null);

public interface IPilotAuthService
{
    Task<PilotLoginResult> LoginAsync(PilotLoginRequest request, CancellationToken cancellationToken = default);
    Task<PilotLoginResult> RegisterAsync(PilotRegisterRequest request, CancellationToken cancellationToken = default);
    Task<MeResult?> GetMeAsync(Guid userId, CancellationToken cancellationToken = default);
}
