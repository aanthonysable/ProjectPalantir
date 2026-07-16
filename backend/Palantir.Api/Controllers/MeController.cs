using Microsoft.AspNetCore.Mvc;
using Palantir.Api;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("me")]
public sealed class MeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            userId = DevDataSeeder.DemoUserId,
            organizationId = DevDataSeeder.DemoOrganizationId,
            displayName = "Demo Pilot User",
            email = "demo@palantir.local",
            authMode = "pilot-placeholder"
        });
    }
}
