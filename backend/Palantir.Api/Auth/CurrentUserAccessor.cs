using System.Security.Claims;

namespace Palantir.Api.Auth;

public interface ICurrentUserAccessor
{
    Guid? UserId { get; }
    Guid? OrganizationId { get; }
    string? AuthMode { get; }
}

/// <summary>
/// Resolves the current pilot user from the Bearer JWT first, then optional
/// X-Palantir-* headers (Development fallback for scripts/tests).
/// </summary>
public sealed class CurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHostEnvironment _environment;

    public CurrentUserAccessor(IHttpContextAccessor httpContextAccessor, IHostEnvironment environment)
    {
        _httpContextAccessor = httpContextAccessor;
        _environment = environment;
    }

    public Guid? UserId =>
        ParseClaim(ClaimTypes.NameIdentifier)
        ?? ParseClaim(JwtRegisteredClaimNamesCompat.Sub)
        ?? (_environment.IsDevelopment() ? ParseHeader("X-Palantir-User-Id") : null);

    public Guid? OrganizationId =>
        ParseClaim("org_id")
        ?? (_environment.IsDevelopment() ? ParseHeader("X-Palantir-Organization-Id") : null);

    public string? AuthMode =>
        _httpContextAccessor.HttpContext?.User.FindFirstValue("auth_mode")
        ?? (_environment.IsDevelopment() && ParseHeader("X-Palantir-User-Id") is not null
            ? "pilot-header-fallback"
            : null);

    private Guid? ParseClaim(string type)
    {
        var value = _httpContextAccessor.HttpContext?.User.FindFirstValue(type);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private Guid? ParseHeader(string name)
    {
        var value = _httpContextAccessor.HttpContext?.Request.Headers[name].FirstOrDefault();
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private static class JwtRegisteredClaimNamesCompat
    {
        public const string Sub = "sub";
    }
}
