using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Palantir.Application.Auth;
using Palantir.Domain.Entities;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Api.Auth;

public sealed class EntraExternalIdAuthService : IEntraExternalIdAuthService
{
    public const string AuthMode = "entra-external-id";

    private readonly PalantirDbContext _db;
    private readonly EntraExternalIdOptions _entra;
    private readonly PilotJwtOptions _pilotJwt;
    private readonly ILogger<EntraExternalIdAuthService> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configManager;

    public EntraExternalIdAuthService(
        PalantirDbContext db,
        IOptions<EntraExternalIdOptions> entra,
        IOptions<PilotJwtOptions> pilotJwt,
        ILogger<EntraExternalIdAuthService> logger)
    {
        _db = db;
        _entra = entra.Value;
        _pilotJwt = pilotJwt.Value;
        _logger = logger;

        if (IsConfigured)
        {
            var metadataAddress = $"{_entra.Authority.TrimEnd('/')}/.well-known/openid-configuration";
            _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = metadataAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase) });
        }
    }

    public bool IsConfigured =>
        _entra.Enabled
        && !string.IsNullOrWhiteSpace(_entra.Authority)
        && !string.IsNullOrWhiteSpace(_entra.ClientId)
        && !string.IsNullOrWhiteSpace(_entra.Audience);

    public AuthProvidersDto GetProviders() =>
        new(
            LocalPasswordEnabled: true,
            EntraExternalId: IsConfigured
                ? new EntraProviderDto(
                    true,
                    _entra.Authority.TrimEnd('/'),
                    _entra.ClientId,
                    _entra.Audience,
                    _entra.TenantId,
                    _entra.Scopes
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray())
                : null);

    public async Task<PilotLoginResult> ExchangeAsync(
        string entraAccessOrIdToken,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || _configManager is null)
        {
            throw new InvalidOperationException(
                "Entra External ID is not configured. Set Authentication:EntraExternalId (Enabled, Authority, ClientId, Audience).");
        }

        if (string.IsNullOrWhiteSpace(entraAccessOrIdToken))
        {
            throw new InvalidOperationException("Entra token is required.");
        }

        var principal = await ValidateEntraTokenAsync(entraAccessOrIdToken, cancellationToken);
        var subject = principal.FindFirstValue("sub")
                      ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? throw new InvalidOperationException("Entra token is missing subject.");
        var issuer = principal.FindFirstValue("iss")
                     ?? _entra.Authority.TrimEnd('/') + "/";
        var email = FirstClaim(principal, "preferred_username", "email", "upn")
                    ?.Trim()
                    .ToLowerInvariant();
        var displayName = FirstClaim(principal, "name", "preferred_username") ?? email ?? "Entra user";
        var tenantId = FirstClaim(principal, "tid");

        var identity = await _db.ExternalIdentities
            .Include(i => i.User)
            .FirstOrDefaultAsync(
                i => i.Issuer == issuer && i.ProviderSubjectId == subject && i.IsLoginEnabled,
                cancellationToken);

        User user;
        if (identity?.User is { IsActive: true } linked)
        {
            user = linked;
            identity.LastVerifiedAt = DateTimeOffset.UtcNow;
            identity.Email = email ?? identity.Email;
            await _db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(email)
                 && await _db.Users.FirstOrDefaultAsync(u => u.IsActive && u.Email.ToLower() == email, cancellationToken)
                     is { } byEmail)
        {
            user = byEmail;
            _db.Add(new ExternalIdentity
            {
                UserId = user.Id,
                Provider = _entra.ProviderName,
                Issuer = issuer,
                ProviderTenantId = tenantId,
                ProviderSubjectId = subject,
                Email = email,
                IsLoginEnabled = true,
                LinkedAt = DateTimeOffset.UtcNow,
                LastVerifiedAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Linked Entra identity {Subject} to existing user {UserId}", subject, user.Id);
        }
        else
        {
            var orgId = await _db.Organizations.OrderBy(o => o.Name).Select(o => o.Id).FirstOrDefaultAsync(cancellationToken);
            if (orgId == Guid.Empty)
            {
                throw new InvalidOperationException("No organization exists to attach the Entra user.");
            }

            user = new User
            {
                OrganizationId = orgId,
                DisplayName = displayName,
                Email = email ?? $"{subject}@entra.local",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Add(user);
            _db.Add(new ExternalIdentity
            {
                UserId = user.Id,
                Provider = _entra.ProviderName,
                Issuer = issuer,
                ProviderTenantId = tenantId,
                ProviderSubjectId = subject,
                Email = email,
                IsLoginEnabled = true,
                LinkedAt = DateTimeOffset.UtcNow,
                LastVerifiedAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Created Palantir user {UserId} from Entra subject {Subject}", user.Id, subject);
        }

        var expiresAt = DateTimeOffset.UtcNow.AddHours(Math.Clamp(_pilotJwt.LifetimeHours, 1, 72));
        return new PilotLoginResult(
            CreatePilotToken(user, expiresAt),
            expiresAt,
            user.Id,
            user.OrganizationId,
            user.DisplayName,
            user.Email,
            AuthMode);
    }

    private async Task<ClaimsPrincipal> ValidateEntraTokenAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var oidc = await _configManager!.GetConfigurationAsync(cancellationToken);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = oidc.Issuer,
            ValidateAudience = true,
            ValidAudiences = new[] { _entra.Audience, _entra.ClientId },
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = oidc.SigningKeys,
            ValidateLifetime = true,
            NameClaimType = "sub",
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        return handler.ValidateToken(token, parameters, out _);
    }

    private string CreatePilotToken(User user, DateTimeOffset expiresAt)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_pilotJwt.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.UniqueName, user.DisplayName),
            new("org_id", user.OrganizationId.ToString()),
            new("auth_mode", AuthMode)
        };

        var jwt = new JwtSecurityToken(
            issuer: _pilotJwt.Issuer,
            audience: _pilotJwt.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static string? FirstClaim(ClaimsPrincipal principal, params string[] types) =>
        types.Select(principal.FindFirstValue).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
