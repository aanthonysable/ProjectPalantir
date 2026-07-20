using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Palantir.Api.Auth;
using Palantir.Application.Ask;

namespace Palantir.Api.Controllers;

[ApiController]
[Authorize]
[Route("ask")]
public sealed class AskController : ControllerBase
{
    private const long MaxAskUploadBytes = 64L * 1024 * 1024;

    private readonly IAskHistoryService _ask;
    private readonly IAskAttachmentService _attachments;
    private readonly ICurrentUserAccessor _currentUser;

    public AskController(
        IAskHistoryService ask,
        IAskAttachmentService attachments,
        ICurrentUserAccessor currentUser)
    {
        _ask = ask;
        _attachments = attachments;
        _currentUser = currentUser;
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<AskSessionSummaryDto>>> List(
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        return Ok(await _ask.ListSessionsAsync(
            _currentUser.OrganizationId.Value,
            _currentUser.UserId.Value,
            cancellationToken));
    }

    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<ActionResult<AskSessionDetailDto>> Get(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        var session = await _ask.GetSessionAsync(
            _currentUser.OrganizationId.Value,
            _currentUser.UserId.Value,
            sessionId,
            cancellationToken);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> Delete(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        try
        {
            await _ask.DeleteSessionAsync(
                _currentUser.OrganizationId.Value,
                _currentUser.UserId.Value,
                sessionId,
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("attachments")]
    [RequestSizeLimit(MaxAskUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxAskUploadBytes)]
    public async Task<ActionResult<IReadOnlyList<AskAttachmentDto>>> UploadAttachments(
        [FromForm] List<IFormFile>? files,
        IFormFile? file,
        [FromForm] Guid? sessionId,
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        var uploads = new List<IFormFile>();
        if (files is { Count: > 0 })
        {
            uploads.AddRange(files.Where(f => f is { Length: > 0 }));
        }

        if (file is { Length: > 0 })
        {
            uploads.Add(file);
        }

        uploads = uploads
            .GroupBy(f => $"{f.FileName}:{f.Length}")
            .Select(g => g.First())
            .ToList();

        if (uploads.Count == 0)
        {
            return BadRequest(new { error = "Choose at least one file." });
        }

        try
        {
            var payloads = new List<(string, string, Stream, long)>();
            var streams = new List<Stream>();
            try
            {
                foreach (var upload in uploads)
                {
                    var stream = upload.OpenReadStream();
                    streams.Add(stream);
                    payloads.Add((
                        upload.FileName,
                        upload.ContentType ?? "application/octet-stream",
                        stream,
                        upload.Length));
                }

                var result = await _attachments.UploadAsync(
                    _currentUser.OrganizationId.Value,
                    _currentUser.UserId.Value,
                    sessionId,
                    payloads,
                    cancellationToken);
                return Ok(result);
            }
            finally
            {
                foreach (var stream in streams)
                {
                    await stream.DisposeAsync();
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("attachments/{attachmentId:guid}/promote")]
    public async Task<ActionResult<AskAttachmentPromoteResult>> Promote(
        Guid attachmentId,
        [FromBody] PromoteAskAttachmentBody? body,
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        try
        {
            var result = await _attachments.PromoteToKnowledgeAsync(
                _currentUser.OrganizationId.Value,
                _currentUser.UserId.Value,
                attachmentId,
                body?.Title,
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed class PromoteAskAttachmentBody
{
    public string? Title { get; set; }
}
