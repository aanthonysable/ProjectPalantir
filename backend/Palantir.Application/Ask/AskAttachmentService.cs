using System.IO.Compression;
using System.Text;
using Palantir.Application.Abstractions;
using Palantir.Application.Knowledge;
using Palantir.Domain.Entities;
using UglyToad.PdfPig;

namespace Palantir.Application.Ask;

public sealed class AskAttachmentService : IAskAttachmentService
{
    private const int MaxFilesPerUpload = 5;
    private const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;
    /// <summary>Files above this are staged to a temp file so multi-GB zips don't OOM.</summary>
    private const long MaxInMemoryBytes = 32L * 1024 * 1024;
    private const long MaxZipUncompressedBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxZipEntryExtractBytes = 64L * 1024 * 1024;
    private const int MaxZipEntries = 40;
    private const int MaxExtractChars = 80_000;

    private static string FormatBytes(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        const double mb = 1024d * 1024d;
        if (bytes >= (long)gb)
        {
            return $"{bytes / gb:0.#} GB";
        }

        return $"{bytes / mb:0.#} MB";
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".log", ".xml", ".html", ".htm"
    };

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    private readonly IPalantirDbContext _db;
    private readonly IBlobKnowledgeStore _blobs;
    private readonly IKnowledgeService _knowledge;
    private readonly IAskAttachmentExtractQueue _extractQueue;

    public AskAttachmentService(
        IPalantirDbContext db,
        IBlobKnowledgeStore blobs,
        IKnowledgeService knowledge,
        IAskAttachmentExtractQueue extractQueue)
    {
        _db = db;
        _blobs = blobs;
        _knowledge = knowledge;
        _extractQueue = extractQueue;
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

            if (file.Length > MaxUploadBytes)
            {
                throw new InvalidOperationException(
                    $"'{file.FileName}' is too large for Ask (max {FormatBytes(MaxUploadBytes)}).");
            }

            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? GuessContentType(file.FileName)
                : file.ContentType.Trim();

            string? tempPath = null;
            try
            {
                Stream working;
                long byteSize = file.Length;
                if (file.Length > MaxInMemoryBytes || IsZip(file.FileName, contentType))
                {
                    tempPath = Path.Combine(Path.GetTempPath(), $"palantir-ask-{Guid.NewGuid():N}");
                    await using (var fs = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await file.Content.CopyToAsync(fs, 1024 * 1024, cancellationToken);
                    }

                    byteSize = new FileInfo(tempPath).Length;
                    if (byteSize <= 0)
                    {
                        continue;
                    }

                    if (byteSize > MaxUploadBytes)
                    {
                        throw new InvalidOperationException(
                            $"'{file.FileName}' is too large for Ask (max {FormatBytes(MaxUploadBytes)}).");
                    }

                    working = new FileStream(
                        tempPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                else
                {
                    var buffer = new MemoryStream();
                    await file.Content.CopyToAsync(buffer, cancellationToken);
                    buffer.Position = 0;
                    working = buffer;
                    byteSize = buffer.Length;
                }

                await using (working)
                {
                    // Store blob first and return Queued — extract runs in the background
                    // (same pattern as knowledge indexing) so the user gets a fast ack.
                    string? blobPath = null;
                    var attachmentId = Guid.NewGuid();
                    if (_blobs.IsConfigured)
                    {
                        blobPath =
                            $"{organizationId:N}/ask-attachments/{userId:N}/{attachmentId:N}/{SanitizeFileName(file.FileName)}";
                        await _blobs.UploadAsync(blobPath, working, contentType, cancellationToken);
                    }
                    else
                    {
                        // Without blob storage, extract inline so Ask still has text.
                        if (working.CanSeek)
                        {
                            working.Position = 0;
                        }

                        var (inlineStatus, inlineText) = Extract(file.FileName, contentType, working);
                        var inlineEntity = new AskAttachment
                        {
                            Id = attachmentId,
                            OrganizationId = organizationId,
                            UserId = userId,
                            SessionId = sessionId,
                            FileName = SanitizeFileName(file.FileName),
                            ContentType = contentType,
                            ByteSize = byteSize,
                            BlobPath = null,
                            ExtractedText = Truncate(inlineText, MaxExtractChars),
                            ExtractStatus = inlineStatus,
                        };
                        _db.Add(inlineEntity);
                        await _db.SaveChangesAsync(cancellationToken);
                        results.Add(Map(inlineEntity));
                        continue;
                    }

                    var entity = new AskAttachment
                    {
                        Id = attachmentId,
                        OrganizationId = organizationId,
                        UserId = userId,
                        SessionId = sessionId,
                        FileName = SanitizeFileName(file.FileName),
                        ContentType = contentType,
                        ByteSize = byteSize,
                        BlobPath = blobPath,
                        ExtractedText = string.Empty,
                        ExtractStatus = "Queued",
                    };
                    _db.Add(entity);
                    await _db.SaveChangesAsync(cancellationToken);
                    await _extractQueue.EnqueueAsync(
                        new AskAttachmentExtractJob(organizationId, entity.Id),
                        cancellationToken);
                    results.Add(Map(entity));
                }
            }
            finally
            {
                if (tempPath is not null)
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // best-effort cleanup
                    }
                }
            }
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

    public async Task<IReadOnlyList<(AskAttachmentDto Meta, string Text)>> GetExtractedForPromptAsync(
        Guid organizationId,
        Guid userId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = LoadOwned(organizationId, userId, ids);
            var pending = rows
                .Where(r => r.ExtractStatus is "Queued" or "Extracting")
                .ToList();
            if (pending.Count == 0)
            {
                IReadOnlyList<(AskAttachmentDto, string)> ready = rows
                    .Select(r => (Map(r), r.ExtractedText ?? string.Empty))
                    .ToList();
                return ready;
            }

            // Kick any stuck Queued rows (e.g. after restart before recover ran).
            foreach (var row in pending.Where(r => r.ExtractStatus == "Queued"))
            {
                await _extractQueue.EnqueueAsync(
                    new AskAttachmentExtractJob(organizationId, row.Id),
                    cancellationToken);
            }

            await Task.Delay(750, cancellationToken);
        }

        var finalRows = LoadOwned(organizationId, userId, ids);
        IReadOnlyList<(AskAttachmentDto, string)> result = finalRows
            .Select(r => (Map(r), r.ExtractedText ?? string.Empty))
            .ToList();
        return result;
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

    public async Task ExtractQueuedAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var row = _db.AskAttachments.FirstOrDefault(a => a.Id == attachmentId);
        if (row is null)
        {
            return;
        }

        if (row.ExtractStatus is not ("Queued" or "Extracting"))
        {
            return;
        }

        row.ExtractStatus = "Extracting";
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            if (string.IsNullOrWhiteSpace(row.BlobPath) || !_blobs.IsConfigured)
            {
                row.ExtractStatus = "Unsupported";
                row.ExtractedText = string.Empty;
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            await using var stream = await _blobs.OpenReadAsync(row.BlobPath, cancellationToken);
            var (status, text) = Extract(row.FileName, row.ContentType, stream);
            row.ExtractStatus = status;
            row.ExtractedText = Truncate(text, MaxExtractChars);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            row.ExtractStatus = "ExtractFailed";
            row.ExtractedText = Truncate($"Extract failed: {ex.Message}", 500);
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }
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
            // Zip attachments expand into multiple knowledge docs via KnowledgeService.
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

        if (IsZip(fileName, contentType))
        {
            return ExtractZip(content, fileName);
        }

        if (PdfExtensions.Contains(ext) || type.Contains("pdf", StringComparison.Ordinal))
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

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = reader.ReadToEnd().Replace("\0", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) ? ("Empty", string.Empty) : ("Ready", text);
    }

    private static (string Status, string Text) ExtractZip(Stream content, string zipFileName)
    {
        try
        {
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (entries.Count == 0)
            {
                return ("Empty", string.Empty);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# ZIP: {Path.GetFileName(zipFileName)}");
            sb.AppendLine($"Entries scanned: {Math.Min(entries.Count, MaxZipEntries)} of {entries.Count}");
            sb.AppendLine();

            long uncompressed = 0;
            var processed = 0;
            var ready = 0;
            var skipped = 0;

            foreach (var entry in entries)
            {
                if (processed >= MaxZipEntries)
                {
                    skipped += entries.Count - processed;
                    break;
                }

                if (ShouldSkipZipEntry(entry.FullName))
                {
                    skipped++;
                    continue;
                }

                var entryName = Path.GetFileName(entry.FullName.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(entryName))
                {
                    skipped++;
                    continue;
                }

                if (string.Equals(Path.GetExtension(entryName), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var ext = Path.GetExtension(entryName);
                var isPdf = PdfExtensions.Contains(ext);
                var isText = TextExtensions.Contains(ext);
                if (!isPdf && !isText)
                {
                    skipped++;
                    continue;
                }

                if (entry.Length > MaxZipEntryExtractBytes)
                {
                    skipped++;
                    sb.AppendLine(
                        $"--- skipped (entry >{FormatBytes(MaxZipEntryExtractBytes)}): {entry.FullName} ---");
                    continue;
                }

                uncompressed += entry.Length;
                if (uncompressed > MaxZipUncompressedBytes)
                {
                    sb.AppendLine(
                        $"--- stopped: uncompressed zip content exceeds {FormatBytes(MaxZipUncompressedBytes)} ---");
                    break;
                }

                processed++;
                using var entryStream = entry.Open();
                using var entryBuffer = new MemoryStream();
                entryStream.CopyTo(entryBuffer);
                entryBuffer.Position = 0;

                string entryText;
                string entryStatus;
                if (isPdf)
                {
                    if (TryExtractPdf(entryBuffer, out var pdf) && !string.IsNullOrWhiteSpace(pdf))
                    {
                        entryStatus = "Ready";
                        entryText = pdf;
                    }
                    else
                    {
                        entryStatus = "Empty";
                        entryText = "(no extractable PDF text)";
                    }
                }
                else
                {
                    using var reader = new StreamReader(
                        entryBuffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                    entryText = reader.ReadToEnd().Replace("\0", string.Empty).Trim();
                    entryStatus = string.IsNullOrWhiteSpace(entryText) ? "Empty" : "Ready";
                    if (entryStatus == "Empty")
                    {
                        entryText = "(empty text file)";
                    }
                }

                if (entryStatus == "Ready")
                {
                    ready++;
                }

                sb.AppendLine($"--- {entry.FullName.Replace('\\', '/')} ({entryStatus}) ---");
                sb.AppendLine(entryText);
                sb.AppendLine();
            }

            var combined = sb.ToString().Trim();
            if (ready == 0)
            {
                return ("Empty", combined);
            }

            var status = skipped > 0 || ready < processed ? "Partial" : "Ready";
            return (status, combined);
        }
        catch (InvalidDataException)
        {
            return ("Unsupported", string.Empty);
        }
        catch
        {
            return ("Unsupported", string.Empty);
        }
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

    private static bool IsZip(string fileName, string? contentType)
    {
        if (string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var type = contentType.ToLowerInvariant();
        return type is "application/zip" or "application/x-zip-compressed" or "multipart/x-zip";
    }

    private static bool ShouldSkipZipEntry(string fullName)
    {
        var normalized = fullName.Replace('\\', '/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = Path.GetFileName(normalized);
        return string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("._", StringComparison.Ordinal);
    }

    private static string GuessContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".md" or ".markdown" => "text/markdown",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };

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
