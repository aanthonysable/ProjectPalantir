using System.Text;
using Palantir.Application.Abstractions;
using Palantir.Application.Knowledge;
using Palantir.Domain.Entities;
using UglyToad.PdfPig;

namespace Palantir.Application.Ask;

public sealed class AskAttachmentService : IAskAttachmentService
{
    private const int MaxFilesPerUpload = 5;
    private const long MaxBytesPerFile = 12L * 1024 * 1024;
    private const int MaxExtractChars = 80_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".log", ".xml", ".html", ".htm"
    };

    private readonly IPalantirDbContext _db;
    private readonly IBlobKnowledgeStore _blobs;
    private readonly IKnowledgeService _knowledge;

    public AskAttachmentService(
        IPalantirDbContext db,
        IBlobKnowledgeStore blobs,
        IKnowledgeService knowledge)
    {
        _db = db;
        _blobs = blobs;
        _knowledge = knowledge;
    }

    public async Task<IReadOnlyList<AskAttachmentDto>> UploadAsync(
        Guid organizationId,
        Guid userId,
        Guid? sessionId,
        IReadOnlyList<(string FileName, string ContentType, Stream Content, long Length)> files,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one file.");
        }

        if (files.Count > MaxFilesPerUpload)
        {
            throw new InvalidOperationException($"Ask allows at most {MaxFilesPerUpload} files per upload.");
        }

        if (sessionId is Guid sid)
        {
            EnsureSessionOwned(organizationId, userId, sid);
        }

        var results = new List<AskAttachmentDto>();
        foreach (var file in files)
        {
            if (file.Length <= 0)
            {
                continue;
            }

            if (file.Length > MaxBytesPerFile)
            {
                throw new InvalidOperationException(
                    $"'{file.FileName}' is too large for Ask (max {MaxBytesPerFile / (1024 * 1024)} MB). " +
                    "Use Admin → Knowledge for large archives.");
            }

            await using var buffer = new MemoryStream();
            await file.Content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType.Trim();
            var (status, text) = Extract(file.FileName, contentType, buffer);
            buffer.Position = 0;

            string? blobPath = null;
            if (_blobs.IsConfigured)
            {
                var id = Guid.NewGuid();
                blobPath = $"{organizationId:N}/ask-attachments/{userId:N}/{id:N}/{SanitizeFileName(file.FileName)}";
                await _blobs.UploadAsync(blobPath, buffer, contentType, cancellationToken);
            }

            var entity = new AskAttachment
            {
                OrganizationId = organizationId,
                UserId = userId,
                SessionId = sessionId,
                FileName = SanitizeFileName(file.FileName),
                ContentType = contentType,
                ByteSize = file.Length,
                BlobPath = blobPath,
                ExtractedText = Truncate(text, MaxExtractChars),
                ExtractStatus = status,
            };
            _db.Add(entity);
            await _db.SaveChangesAsync(cancellationToken);
            results.Add(Map(entity));
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException("No usable files were uploaded.");
        }

        return results;
    }

    public Task<IReadOnlyList<AskAttachmentDto>> GetAsync(
        Guid organizationId,
        Guid userId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var rows = LoadOwned(organizationId, userId, ids);
        IReadOnlyList<AskAttachmentDto> result = rows.Select(Map).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<(AskAttachmentDto Meta, string Text)>> GetExtractedForPromptAsync(
        Guid organizationId,
        Guid userId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var rows = LoadOwned(organizationId, userId, ids);
        IReadOnlyList<(AskAttachmentDto, string)> result = rows
            .Select(r => (Map(r), r.ExtractedText ?? string.Empty))
            .ToList();
        return Task.FromResult(result);
    }

    public async Task BindToSessionAsync(
        Guid organizationId,
        Guid userId,
        Guid sessionId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return;
        }

        EnsureSessionOwned(organizationId, userId, sessionId);
        var rows = LoadOwned(organizationId, userId, ids);
        foreach (var row in rows)
        {
            row.SessionId ??= sessionId;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AskAttachmentPromoteResult> PromoteToKnowledgeAsync(
        Guid organizationId,
        Guid userId,
        Guid attachmentId,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        var row = LoadOwned(organizationId, userId, [attachmentId]).FirstOrDefault()
            ?? throw new InvalidOperationException("Attachment not found.");

        if (row.KnowledgeDocumentId is not null)
        {
            return new AskAttachmentPromoteResult(Map(row), null);
        }

        if (!_knowledge.IsStorageConfigured && string.IsNullOrWhiteSpace(row.ExtractedText))
        {
            throw new InvalidOperationException(
                "Knowledge storage is not configured and this file has no extracted text to save.");
        }

        KnowledgeUploadResult uploaded;
        var docTitle = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(row.FileName)
            : title.Trim();

        if (!string.IsNullOrWhiteSpace(row.BlobPath) && _blobs.IsConfigured)
        {
            await using var stream = await _blobs.OpenReadAsync(row.BlobPath, cancellationToken);
            uploaded = (await _knowledge.UploadAsync(
                organizationId,
                userId,
                row.FileName,
                row.ContentType,
                stream,
                docTitle,
                cancellationToken)).Results.First();
        }
        else
        {
            var markdown =
                $"# {docTitle}\n\n" +
                $"_Promoted from Ask attachment `{row.FileName}` on {DateTimeOffset.UtcNow:u}._\n\n" +
                row.ExtractedText;
            await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
            uploaded = (await _knowledge.UploadAsync(
                organizationId,
                userId,
                $"{SanitizeFileName(docTitle)}.md",
                "text/markdown",
                ms,
                docTitle,
                cancellationToken)).Results.First();
        }

        row.KnowledgeDocumentId = uploaded.Document.Id;
        await _db.SaveChangesAsync(cancellationToken);
        return new AskAttachmentPromoteResult(Map(row), uploaded);
    }

    private List<AskAttachment> LoadOwned(Guid organizationId, Guid userId, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var idSet = ids.Distinct().ToHashSet();
        return _db.AskAttachments
            .Where(a => a.OrganizationId == organizationId && a.UserId == userId && idSet.Contains(a.Id))
            .OrderBy(a => a.CreatedAt)
            .ToList();
    }

    private void EnsureSessionOwned(Guid organizationId, Guid userId, Guid sessionId)
    {
        var ok = _db.AskSessions.Any(s =>
            s.Id == sessionId && s.OrganizationId == organizationId && s.UserId == userId);
        if (!ok)
        {
            throw new InvalidOperationException("Ask session not found.");
        }
    }

    private static (string Status, string Text) Extract(string fileName, string contentType, Stream content)
    {
        var ext = Path.GetExtension(fileName);
        var type = contentType.ToLowerInvariant();

        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("pdf", StringComparison.Ordinal))
        {
            if (TryExtractPdf(content, out var pdf) && !string.IsNullOrWhiteSpace(pdf))
            {
                return ("Ready", pdf);
            }

            return ("Empty", string.Empty);
        }

        var looksText =
            TextExtensions.Contains(ext) ||
            type.StartsWith("text/", StringComparison.Ordinal) ||
            type is "application/json" or "application/xml" or "application/markdown";

        if (!looksText)
        {
            return ("Unsupported", string.Empty);
        }

        content.Position = 0;
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = reader.ReadToEnd().Replace("\0", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) ? ("Empty", string.Empty) : ("Ready", text);
    }

    private static bool TryExtractPdf(Stream content, out string text)
    {
        text = string.Empty;
        try
        {
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            using var doc = PdfDocument.Open(content);
            var sb = new StringBuilder();
            foreach (var page in doc.GetPages())
            {
                sb.AppendLine(page.Text);
            }

            text = sb.ToString().Replace("\0", string.Empty).Trim();
            return text.Length > 0;
        }
        catch
        {
            text = string.Empty;
            return false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var trimmed = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "upload.bin";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(c, '_');
        }

        return trimmed.Length <= 180 ? trimmed : trimmed[..180];
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? value
        : value.Length <= max ? value
        : value[..max] + "\n…[truncated for Ask]";

    private static AskAttachmentDto Map(AskAttachment a) =>
        new(
            a.Id,
            a.FileName,
            a.ContentType,
            a.ByteSize,
            a.ExtractStatus,
            a.ExtractedText?.Length ?? 0,
            a.SessionId,
            a.KnowledgeDocumentId,
            a.CreatedAt);
}
