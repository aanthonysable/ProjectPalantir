using Microsoft.AspNetCore.Identity;
using Palantir.Domain.Entities;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Api;

public static class DevDataSeeder
{
    public static readonly Guid DemoOrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid DemoUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid AlecUserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    public const string DemoEmail = "demo@palantir.local";
    public const string DemoPassword = "pilot-demo";

    public const string AlecEmail = "alec.anthony@dnow.com";
    public const string AlecDisplayName = "Alec Anthony";
    public const string AlecPassword = "pilot-demo";

    public static async Task SeedAsync(PalantirDbContext db, IServiceProvider services)
    {
        if (!db.Organizations.Any())
        {
            var org = new Organization
            {
                Id = DemoOrganizationId,
                Name = "Sable Automation Solutions"
            };

            var demo = new User
            {
                Id = DemoUserId,
                OrganizationId = DemoOrganizationId,
                DisplayName = "Demo Pilot User",
                Email = DemoEmail
            };

            var identity = new ExternalIdentity
            {
                UserId = DemoUserId,
                Provider = "palantir-pilot",
                Issuer = "https://login.palantir.local/",
                ProviderSubjectId = "demo-subject-001",
                Email = demo.Email,
                IsLoginEnabled = true
            };

            db.Add(org);
            db.Add(demo);
            db.Add(identity);
            await db.SaveChangesAsync();
        }

        await EnsureUserCredentialAsync(db, DemoUserId, DemoEmail, "Demo Pilot User", "demo-subject-001", DemoPassword);
        await EnsureUserCredentialAsync(db, AlecUserId, AlecEmail, AlecDisplayName, "alec-dnow-001", AlecPassword);
        await TransferOutlookToAlecIfNeededAsync(db);
    }

    private static async Task EnsureUserCredentialAsync(
        PalantirDbContext db,
        Guid userId,
        string email,
        string displayName,
        string subjectId,
        string password)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var user = db.Users.FirstOrDefault(u => u.Id == userId)
                   ?? db.Users.FirstOrDefault(u => u.Email.ToLower() == normalized);

        if (user is null)
        {
            user = new User
            {
                Id = userId,
                OrganizationId = DemoOrganizationId,
                DisplayName = displayName,
                Email = normalized,
                IsActive = true
            };
            db.Add(user);

            db.Add(new ExternalIdentity
            {
                UserId = userId,
                Provider = "palantir-pilot",
                Issuer = "https://login.palantir.local/",
                ProviderSubjectId = subjectId,
                Email = normalized,
                IsLoginEnabled = true
            });
            await db.SaveChangesAsync();
        }
        else
        {
            user.Email = normalized;
            user.DisplayName = displayName;
            user.IsActive = true;
        }

        var hasher = new PasswordHasher<User>();
        var hash = hasher.HashPassword(user, password);
        var existing = db.LocalPilotCredentials.FirstOrDefault(c => c.UserId == user.Id);
        if (existing is null)
        {
            db.Add(new LocalPilotCredential
            {
                UserId = user.Id,
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

    /// <summary>
    /// One-time: move Outlook connector from demo user onto Alec so work-email login keeps the mailbox.
    /// </summary>
    private static async Task TransferOutlookToAlecIfNeededAsync(PalantirDbContext db)
    {
        var alec = db.Users.FirstOrDefault(u => u.Email.ToLower() == AlecEmail);
        if (alec is null)
        {
            return;
        }

        if (db.ConnectedAccounts.Any(a => a.UserId == alec.Id))
        {
            return;
        }

        var demoAccounts = db.ConnectedAccounts.Where(a => a.UserId == DemoUserId).ToList();
        if (demoAccounts.Count == 0)
        {
            return;
        }

        foreach (var account in demoAccounts)
        {
            account.UserId = alec.Id;
        }

        await db.SaveChangesAsync();
    }
}
