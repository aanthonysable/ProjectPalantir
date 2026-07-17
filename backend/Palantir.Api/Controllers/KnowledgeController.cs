using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Palantir.Api.Auth;
using Palantir.Application.Knowledge;
using Palantir.Application.Outbound;

namespace Palantir.Api.Controllers;

[ApiController]
[Authorize]
[Route("knowledge")]
public sealed class KnowledgeController : ControllerBase
{
    private readonly IKnowledgeService _knowledge;
    private readonly IKnowledgeCaptureService _capture;
    private readonly ICurrentUserAccessor _currentUser;

    public KnowledgeController(
        IKnowledgeService knowledge,
        IKnowledgeCaptureService capture,
        ICurrentUserAccessor currentUser)
    {
        _knowledge = knowledge;
        _capture = capture;
        _currentUser = currentUser;
    }

    [HttpGet("status")]
    public ActionResult<object> Status() =>
        Ok(new
        {
            storageConfigured = _knowledge.IsStorageConfigured,
            container = "knowledge"
        });

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<KnowledgeDocumentDto>>> List(
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null)
        {
            return BadRequest("Organization id is required.");
        }

        var items = await _knowledge.ListAsync(_currentUser.OrganizationId.Value, cancellationToken);
        return Ok(items);
    }

    // Allow multi-GB PLC programs / archives (matches Kestrel + FormOptions in Program.cs).
    private const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;

    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<ActionResult<KnowledgeUploadBatchResult>> Upload(
        [FromForm] List<IFormFile>? files,
        IFormFile? file,
        [FromForm] string? title,
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return BadRequest("Organization and user ids are required.");
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

        // De-dupe if the same file was bound to both "file" and "files".
        uploads = uploads
            .GroupBy(f => $"{f.FileName}:{f.Length}")
            .Select(g => g.First())
            .ToList();

        if (uploads.Count == 0)
        {
            return BadRequest(new { error = "Choose one or more non-empty files to upload." });
        }

        if (uploads.Count > 40)
        {
            return BadRequest(new { error = "Upload at most 40 files at once." });
        }

        try
        {
            var orgId = _currentUser.OrganizationId.Value;
            var userId = _currentUser.UserId.Value;
            var allResults = new List<KnowledgeUploadResult>();
            var allNotes = new List<string>();
            var skipped = 0;

            for (var i = 0; i < uploads.Count; i++)
            {
                var upload = uploads[i];
                var fileTitle = uploads.Count == 1
                    ? title
                    : string.IsNullOrWhiteSpace(title)
                        ? null
                        : $"{title.Trim()} / {upload.FileName}";

                await using var stream = upload.OpenReadStream();
                var batch = await _knowledge.UploadAsync(
                    orgId,
                    userId,
                    upload.FileName,
                    upload.ContentType,
                    stream,
                    fileTitle,
                    cancellationToken);
                allResults.AddRange(batch.Results);
                skipped += batch.SkippedEntries;
                allNotes.AddRange(batch.Notes);
            }

            if (uploads.Count > 1)
            {
                allNotes.Insert(
                    0,
                    $"Uploaded {uploads.Count} files → {allResults.Count} document(s), skipped {skipped}.");
            }

            return Ok(new KnowledgeUploadBatchResult(allResults, skipped, allNotes));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Queue an approval-gated AI/human knowledge capture into org memory.</summary>
    [HttpPost("capture")]
    public async Task<ActionResult<ReplyDraftResult>> Capture(
        [FromBody] ProposeKnowledgeCaptureBody body,
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return BadRequest("Organization and user ids are required.");
        }

        try
        {
            var result = await _capture.ProposeAsync(
                new ProposeKnowledgeCaptureRequest(
                    _currentUser.OrganizationId.Value,
                    _currentUser.UserId.Value,
                    body.Title ?? string.Empty,
                    body.Body ?? string.Empty,
                    body.SourceQuestion,
                    body.CreatedByAi ?? true),
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{documentId:guid}")]
    public async Task<IActionResult> Delete(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null)
        {
            return BadRequest("Organization id is required.");
        }

        try
        {
            await _knowledge.DeleteAsync(_currentUser.OrganizationId.Value, documentId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed class ProposeKnowledgeCaptureBody
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? SourceQuestion { get; set; }
    public bool? CreatedByAi { get; set; }
}
