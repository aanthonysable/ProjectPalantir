using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Palantir.Application.Auth;
using Palantir.Domain.Entities;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Api.Auth;

public sealed class PilotAuthService : IPilotAuthService
{
    public const string AuthMode = "pilot-local";

    private readonly PalantirDbContext _db;
    private readonly PilotJwtOptions _options;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public PilotAuthService(PalantirDbContext db, IOptions<PilotJwtOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<PilotLoginResult> LoginAsync(
        PilotLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new InvalidOperationException("Email and password are required.");
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users
            .Where(u => u.IsActive && u.Email.ToLower() == email)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Invalid email or password.");

        var credential = await _db.LocalPilotCredentials
            .FirstOrDefaultAsync(c => c.UserId == user.Id, cancellationToken)
            ?? throw new InvalidOperationException("Invalid email or password.");

        var verify = _passwordHasher.VerifyHashedPassword(user, credential.PasswordHash, request.Password);
        if (verify == PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("Invalid email or password.");
        }

        if (verify == PasswordVerificationResult.SuccessRehashNeeded)
        {
            credential.PasswordHash = _passwordHasher.HashPassword(user, request.Password);
            credential.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var expiresAt = DateTimeOffset.UtcNow.AddHours(Math.Clamp(_options.LifetimeHours, 1, 72));
        var token = CreateToken(user, expiresAt);

        return new PilotLoginResult(
            token,
            expiresAt,
            user.Id,
            user.OrganizationId,
            user.DisplayName,
            user.Email,
            AuthMode);
    }

    public async Task<MeResult?> GetMeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users
            .Where(u => u.Id == userId && u.IsActive)
            .FirstOrDefaultAsync(cancellationToken);

        return user is null
            ? null
            : new MeResult(user.Id, user.OrganizationId, user.DisplayName, user.Email, AuthMode);
    }

    private string CreateToken(User user, DateTimeOffset expiresAt)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.UniqueName, user.DisplayName),
            new("org_id", user.OrganizationId.ToString()),
            new("auth_mode", AuthMode)
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
