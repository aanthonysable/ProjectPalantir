namespace Palantir.Api.Auth;

public interface ICurrentUserAccessor
{
    Guid? UserId { get; }
    Guid? OrganizationId { get; }
}

/// <summary>
/// Development placeholder until Entra External ID / pilot IdP is wired.
/// Reads X-Palantir-User-Id and X-Palantir-Organization-Id headers.
/// </summary>
public sealed class CurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId => ParseHeader("X-Palantir-User-Id");
    public Guid? OrganizationId => ParseHeader("X-Palantir-Organization-Id");

    private Guid? ParseHeader(string name)
    {
        var value = _httpContextAccessor.HttpContext?.Request.Headers[name].FirstOrDefault();
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
