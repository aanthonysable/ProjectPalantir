using Microsoft.AspNetCore.Identity;
using Palantir.Domain.Entities;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Api;

public static class DevDataSeeder
{
    public static readonly Guid DemoOrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid DemoUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    /// <summary>Local pilot password for demo@palantir.local — change in shared environments.</summary>
    public const string DemoPassword = "pilot-demo";

    public static async Task SeedAsync(PalantirDbContext db, IServiceProvider services)
    {
        if (!db.Organizations.Any())
        {
            var org = new Organization
            {
                Id = DemoOrganizationId,
                Name = "Sable Automation Solutions"
            };

            var user = new User
            {
                Id = DemoUserId,
                OrganizationId = DemoOrganizationId,
                DisplayName = "Demo Pilot User",
                Email = "demo@palantir.local"
            };

            var identity = new ExternalIdentity
            {
                UserId = DemoUserId,
                Provider = "palantir-pilot",
                Issuer = "https://login.palantir.local/",
                ProviderSubjectId = "demo-subject-001",
                Email = user.Email,
                IsLoginEnabled = true
            };

            db.Add(org);
            db.Add(user);
            db.Add(identity);
            await db.SaveChangesAsync();
        }

        await EnsureDemoCredentialAsync(db);
    }

    private static async Task EnsureDemoCredentialAsync(PalantirDbContext db)
    {
        var user = db.Users.FirstOrDefault(u => u.Id == DemoUserId);
        if (user is null)
        {
            return;
        }

        var existing = db.LocalPilotCredentials.FirstOrDefault(c => c.UserId == DemoUserId);
        var hasher = new PasswordHasher<User>();
        var hash = hasher.HashPassword(user, DemoPassword);

        if (existing is null)
        {
            db.Add(new LocalPilotCredential
            {
                UserId = DemoUserId,
                PasswordHash = hash,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.PasswordHash = hash;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }
}
