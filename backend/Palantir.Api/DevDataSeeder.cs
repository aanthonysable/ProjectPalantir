using Palantir.Domain.Entities;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Api;

public static class DevDataSeeder
{
    public static readonly Guid DemoOrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid DemoUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public static async Task SeedAsync(PalantirDbContext db)
    {
        if (db.Organizations.Any())
        {
            return;
        }

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
}
