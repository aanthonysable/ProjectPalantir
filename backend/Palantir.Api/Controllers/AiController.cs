using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Palantir.Application.Ai;

namespace Palantir.Api.Controllers;

[ApiController]
[Authorize]
[Route("ai")]
public sealed class AiController : ControllerBase
{
    private readonly IAiCompletionClient _ai;

    public AiController(IAiCompletionClient ai)
    {
        _ai = ai;
    }

    [HttpGet("status")]
    public ActionResult<AiStatusDto> Status() => Ok(_ai.GetStatus());
}
