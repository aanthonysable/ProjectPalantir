using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Palantir.Application.Abstractions;
using Palantir.Application.Ai;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;
using UglyToad.PdfPig;

namespace Palantir.Application.Knowledge;

public sealed class KnowledgeService : IKnowledgeService
{
    /// <summary>Per-file and ZIP envelope limit (PLC programs can be multi-GB).</summary>
    private const long MaxSingleFileBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxZipBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxZipUncompressedBytes = 4L * 1024 * 1024 * 1024;
    /// <summary>Text/images still buffered in memory for indexing; larger files are stored only.</summary>
    private const long MaxInMemoryIndexBytes = 32L * 1024 * 1024;
    private const long MaxVisionImageBytes = 5 * 1024 * 1024;
    private const int MaxZipEntries = 200;

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

    private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".json", ".log", ".xml", ".html", ".htm"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    /// <summary>Stored in blob for later indexing; not text-parsed in this pilot slice.</summary>
    private static readonly HashSet<string> PlcProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".acd", ".l5x", ".l5k", ".rss", ".st", ".scl", ".awl", ".apo",
        ".zap13", ".zap14", ".zap15", ".zap16", ".project", ".tsproj",
        ".plc", ".sx", ".smbp", ".gsd", ".gsdml"
    };

    private readonly IPalantirDbContext _db;
    private readonly IBlobKnowledgeStore _blobs;
    private readonly IAiCompletionClient _ai;
    private readonly IAuditEventWriter _audit;
    private readonly IKnowledgeIndexQueue _indexQueue;

    public KnowledgeService(
        IPalantirDbContext db,
        IBlobKnowledgeStore blobs,
        IAiCompletionClient ai,
        IAuditEventWriter audit,
        IKnowledgeIndexQueue indexQueue)
    {
        _db = db;
        _blobs = blobs;
        _ai = ai;
        _audit = audit;
        _indexQueue = indexQueue;
    }

    public bool IsStorageConfigured => _blobs.IsConfigured;

    public Task<IReadOnlyList<KnowledgeDocumentDto>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var docs = _db.KnowledgeDocuments
            .Where(d => d.OrganizationId == organizationId)
            .ToList()
            .OrderByDescending(d => d.CreatedAt)
            .ToList();

        var chunkCounts = _db.KnowledgeChunks
            .Where(c => docs.Select(d => d.Id).Contains(c.DocumentId))
            .ToList()
            .GroupBy(c => c.DocumentId)
            .ToDictionary(g => g.Key, g => g.Count());

        IReadOnlyList<KnowledgeDocumentDto> items = docs
            .Select(d => Map(d, chunkCounts.GetValueOrDefault(d.Id)))
            .ToList();
        return Task.FromResult(items);
    }

    public async Task<KnowledgeUploadBatchResult> UploadAsync(
        Guid organizationId,
        Guid userId,
        string fileName,
        string contentType,
        Stream content,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (!_blobs.IsConfigured)
        {
            throw new InvalidOperationException(
                "Azure Blob Storage is not configured. Set Azure:Storage:ConnectionString in user-secrets.");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("File name is required.");
        }

        var safeName = Path.GetFileName(fileName.Trim());
        string? tempPath = null;
        Stream working = content;
        try
        {
            // Large multipart uploads are disk-backed by ASP.NET; avoid copying into RAM.
            if (!content.CanSeek)
            {
                tempPath = Path.Combine(
                    Path.GetTempPath(),
                    $"palantir-knowledge-{Guid.NewGuid():N}");
                await using (var fs = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await content.CopyToAsync(fs, 1024 * 1024, cancellationToken);
                }

                working = new FileStream(
                    tempPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            else if (content.Position != 0)
            {
                content.Position = 0;
            }

            var length = working.CanSeek ? working.Length : -1;
            if (length == 0)
            {
                throw new InvalidOperationException("File is empty.");
            }

            if (IsZip(safeName, contentType))
            {
                if (length > MaxZipBytes)
                {
                    throw new InvalidOperationException(
                        $"ZIP exceeds the {FormatBytes(MaxZipBytes)} limit.");
                }

                return await UploadZipAsync(
                    organizationId,
                    userId,
                    safeName,
                    working,
                    title,
                    cancellationToken);
            }

            if (length > MaxSingleFileBytes)
            {
                throw new InvalidOperationException(
                    $"File exceeds the {FormatBytes(MaxSingleFileBytes)} limit.");
            }

            var single = await IngestFileAsync(
                organizationId,
                userId,
                safeName,
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                working,
                title,
                cancellationToken);
            return new KnowledgeUploadBatchResult([single], 0, []);
        }
        finally
        {
            if (!ReferenceEquals(working, content))
            {
                await working.DisposeAsync();
            }

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

    private async Task<KnowledgeUploadBatchResult> UploadZipAsync(
        Guid organizationId,
        Guid userId,
        string zipFileName,
        Stream zipStream,
        string? titlePrefix,
        CancellationToken cancellationToken)
    {
        var results = new List<KnowledgeUploadResult>();
        var notes = new List<string>();
        var skipped = 0;
        long uncompressedTotal = 0;
        var processed = 0;

        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (entries.Count == 0)
            {
                throw new InvalidOperationException("ZIP has no files to process.");
            }

            if (entries.Count > MaxZipEntries)
            {
                throw new InvalidOperationException(
                    $"ZIP has {entries.Count} files; pilot limit is {MaxZipEntries} entries.");
            }

            var zipLabel = Path.GetFileNameWithoutExtension(zipFileName);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

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
                    notes.Add($"Skipped nested zip: {entry.FullName}");
                    continue;
                }

                var isText = IsIndexableFile(entryName, contentType: null);
                var isImage = IsImageFile(entryName, contentType: null);
                var isPlc = IsPlcProgramFile(entryName);
                if (!isText && !isImage && !isPlc)
                {
                    skipped++;
                    notes.Add($"Skipped unsupported file: {entry.FullName}");
                    continue;
                }

                if (entry.Length > MaxSingleFileBytes)
                {
                    skipped++;
                    notes.Add(
                        $"Skipped oversized file (>{FormatBytes(MaxSingleFileBytes)}): {entry.FullName}");
                    continue;
                }

                uncompressedTotal += entry.Length;
                if (uncompressedTotal > MaxZipUncompressedBytes)
                {
                    throw new InvalidOperationException(
                        $"ZIP uncompressed content exceeds the {FormatBytes(MaxZipUncompressedBytes)} limit.");
                }

                // Stream large zip entries via temp file so multi-GB PLC programs don't OOM.
                if (entry.Length > MaxInMemoryIndexBytes || IsPlcProgramFile(entryName))
                {
                    var entryTemp = Path.Combine(
                        Path.GetTempPath(),
                        $"palantir-zip-entry-{Guid.NewGuid():N}");
                    try
                    {
                        await using (var entryStream = entry.Open())
                        await using (var fs = new FileStream(
                            entryTemp,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 1024 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            await entryStream.CopyToAsync(fs, 1024 * 1024, cancellationToken);
                        }

                        if (new FileInfo(entryTemp).Length == 0)
                        {
                            skipped++;
                            continue;
                        }

                        await using var entryFile = new FileStream(
                            entryTemp,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 1024 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);

                        var relativeLarge = entry.FullName.Replace('\\', '/').Trim('/');
                        var docTitleLarge = string.IsNullOrWhiteSpace(titlePrefix)
                            ? $"{zipLabel} / {relativeLarge}"
                            : $"{titlePrefix.Trim()} / {relativeLarge}";

                        var ingestedLarge = await IngestFileAsync(
                            organizationId,
                            userId,
                            relativeLarge.Replace('/', '_'),
                            GuessContentType(entryName),
                            entryFile,
                            docTitleLarge,
                            cancellationToken);
                        results.Add(ingestedLarge);
                        processed++;
                    }
                    finally
                    {
                        try
                        {
                            File.Delete(entryTemp);
                        }
                        catch
                        {
                            // best-effort cleanup
                        }
                    }

                    continue;
                }

                await using var entryBuffer = new MemoryStream();
                await using (var entryStream = entry.Open())
                {
                    await entryStream.CopyToAsync(entryBuffer, cancellationToken);
                }

                if (entryBuffer.Length == 0)
                {
                    skipped++;
                    continue;
                }

                entryBuffer.Position = 0;
                var relative = entry.FullName.Replace('\\', '/').Trim('/');
                var docTitle = string.IsNullOrWhiteSpace(titlePrefix)
                    ? $"{zipLabel} / {relative}"
                    : $"{titlePrefix.Trim()} / {relative}";

                var ingested = await IngestFileAsync(
                    organizationId,
                    userId,
                    relative.Replace('/', '_'),
                    GuessContentType(entryName),
                    entryBuffer,
                    docTitle,
                    cancellationToken);
                results.Add(ingested);
                processed++;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("ZIP could not be read. Confirm it is a valid .zip archive.", ex);
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException(
                skipped > 0
                    ? $"ZIP contained no supported files ({skipped} skipped). Include text, images, or PLC program files."
                    : "ZIP contained no files to index.");
        }

        notes.Insert(0, $"ZIP “{zipFileName}”: processed {results.Count} file(s), skipped {skipped}.");
        await _audit.WriteAsync(
            organizationId,
            "knowledge.zip_uploaded",
            userId,
            "KnowledgeZip",
            null,
            cancellationToken: cancellationToken);

        return new KnowledgeUploadBatchResult(results, skipped, notes);
    }

    private async Task<KnowledgeUploadResult> IngestFileAsync(
        Guid organizationId,
        Guid userId,
        string fileName,
        string contentType,
        Stream content,
        string? title,
        CancellationToken cancellationToken)
    {
        if (content.CanSeek && content.Position != 0)
        {
            content.Position = 0;
        }

        var byteSize = content.CanSeek ? content.Length : -1;
        if (byteSize == 0)
        {
            throw new InvalidOperationException($"File '{fileName}' is empty.");
        }

        if (byteSize > MaxSingleFileBytes)
        {
            throw new InvalidOperationException(
                $"File '{fileName}' exceeds the {FormatBytes(MaxSingleFileBytes)} limit.");
        }

        var safeName = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "document.txt";
        }

        var displayName = fileName.Contains('/') || fileName.Contains('_')
            ? fileName.Replace('_', '/').Trim()
            : safeName;

        var resolvedContentType = string.IsNullOrWhiteSpace(contentType)
            ? GuessContentType(safeName)
            : contentType;

        var isPlc = IsPlcProgramFile(safeName);
        var isImage = IsImageFile(safeName, resolvedContentType);
        var isPdf = IsPdfFile(safeName, resolvedContentType);
        var isText = IsIndexableFile(safeName, resolvedContentType) && !isPdf;
        var canQueueIndex = !isPlc && (isPdf || isImage || isText);

        var docId = Guid.NewGuid();
        var blobPath = $"{organizationId:N}/{docId:N}/{safeName}";
        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(safeName)
            : title.Trim();
        if (string.IsNullOrWhiteSpace(resolvedTitle))
        {
            resolvedTitle = safeName;
        }

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        await _blobs.UploadAsync(blobPath, content, resolvedContentType, cancellationToken);

        if (byteSize <= 0 && content.CanSeek)
        {
            byteSize = content.Length;
        }

        var document = new KnowledgeDocument
        {
            Id = docId,
            OrganizationId = organizationId,
            Title = resolvedTitle.Length <= 300 ? resolvedTitle : resolvedTitle[..300],
            FileName = displayName.Length <= 260 ? displayName : safeName,
            ContentType = resolvedContentType,
            BlobPath = blobPath,
            ByteSize = byteSize > 0 ? byteSize : 0,
            Status = isPlc ? "StoredOnly" : canQueueIndex ? "Queued" : "StoredOnly",
            IndexError = isPlc
                ? "PLC program stored for future knowledge indexing (not parsed in this pilot)."
                : canQueueIndex
                    ? null
                    : "Stored in blob, but this file type is not indexed yet.",
            UploadedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Add(document);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            organizationId,
            "knowledge.document_uploaded",
            userId,
            nameof(KnowledgeDocument),
            document.Id,
            cancellationToken: cancellationToken);

        if (canQueueIndex)
        {
            await _indexQueue.EnqueueAsync(
                new KnowledgeIndexJob(organizationId, document.Id),
                cancellationToken);
        }

        return new KnowledgeUploadResult(Map(document, 0), Indexed: false);
    }

    public async Task IndexQueuedDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var document = _db.KnowledgeDocuments.FirstOrDefault(d => d.Id == documentId);
        if (document is null)
        {
            return;
        }

        if (document.Status is not ("Queued" or "Indexing"))
        {
            return;
        }

        document.Status = "Indexing";
        document.IndexError = null;
        document.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await using var blobStream = await _blobs.OpenReadAsync(document.BlobPath, cancellationToken);
            await IndexDocumentContentAsync(document, blobStream, cancellationToken);
        }
        catch (Exception ex)
        {
            document.Status = "IndexFailed";
            document.IndexError = TruncateError($"Indexing failed: {ex.Message}");
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task IndexDocumentContentAsync(
        KnowledgeDocument document,
        Stream content,
        CancellationToken cancellationToken)
    {
        var safeName = Path.GetFileName(document.FileName.Replace('/', '_'));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = document.FileName;
        }

        var isImage = IsImageFile(safeName, document.ContentType);
        var isPdf = IsPdfFile(safeName, document.ContentType);

        // Clear any prior chunks if re-indexing.
        var existingChunks = _db.KnowledgeChunks.Where(c => c.DocumentId == document.Id).ToList();
        foreach (var chunk in existingChunks)
        {
            _db.Remove(chunk);
        }

        if (isPdf)
        {
            if (TryExtractPdfText(content, out var pdfText))
            {
                var chunks = ChunkText(pdfText);
                if (chunks.Count == 0)
                {
                    document.Status = "IndexedEmpty";
                    document.IndexError = "No searchable text was found in the PDF.";
                }
                else
                {
                    AddChunks(document.Id, chunks);
                    document.Status = "Indexed";
                    document.IndexError = null;
                }
            }
            else
            {
                document.Status = "StoredOnly";
                document.IndexError =
                    "PDF stored. No extractable text was found — scanned/image-only PDFs are not indexed yet.";
            }
        }
        else if (isImage)
        {
            if (document.ByteSize > MaxInMemoryIndexBytes)
            {
                document.Status = "StoredOnly";
                document.IndexError =
                    $"Image stored. Vision indexing is limited to {FormatBytes(MaxInMemoryIndexBytes)} in this pilot.";
            }
            else
            {
                await using var buffer = new MemoryStream();
                if (content.CanSeek)
                {
                    content.Position = 0;
                }

                await content.CopyToAsync(buffer, cancellationToken);
                buffer.Position = 0;
                await DescribeAndIndexImageAsync(
                    document, buffer, safeName, document.ContentType, cancellationToken);
            }
        }
        else
        {
            if (document.ByteSize > MaxInMemoryIndexBytes)
            {
                document.Status = "StoredOnly";
                document.IndexError =
                    $"File stored. Text indexing is limited to {FormatBytes(MaxInMemoryIndexBytes)} in this pilot.";
            }
            else
            {
                await using var buffer = new MemoryStream();
                if (content.CanSeek)
                {
                    content.Position = 0;
                }

                await content.CopyToAsync(buffer, cancellationToken);
                buffer.Position = 0;
                if (TryExtractText(safeName, document.ContentType, buffer, out var text))
                {
                    var chunks = ChunkText(text);
                    if (chunks.Count == 0)
                    {
                        document.Status = "IndexedEmpty";
                        document.IndexError = "No searchable text was found in the file.";
                    }
                    else
                    {
                        AddChunks(document.Id, chunks);
                        document.Status = "Indexed";
                        document.IndexError = null;
                    }
                }
                else
                {
                    document.Status = "StoredOnly";
                    document.IndexError = "Stored in blob, but this file type is not indexed yet.";
                }
            }
        }

        document.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> DescribeAndIndexImageAsync(
        KnowledgeDocument document,
        MemoryStream imageBytes,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (imageBytes.Length > MaxVisionImageBytes)
        {
            document.Status = "StoredOnly";
            document.IndexError =
                $"Image stored, but exceeds the {MaxVisionImageBytes / (1024 * 1024)} MB vision limit for indexing.";
            return 0;
        }

        if (!_ai.IsConfiguredFor(AiTaskKind.DescribeImage) && !_ai.IsConfigured)
        {
            document.Status = "StoredOnly";
            document.IndexError =
                "Image stored. Configure Gemini (Ai:Tasks:DescribeImage) to index diagrams/photos.";
            return 0;
        }

        try
        {
            imageBytes.Position = 0;
            var bytes = imageBytes.ToArray();
            var mediaType = NormalizeImageMediaType(fileName, contentType);
            var dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";

            var description = (await _ai.CompleteAsync(
                AiTaskKind.DescribeImage,
                [
                    new AiChatMessage(
                        "user",
                        "Describe this industrial / ops image for a searchable knowledge base.",
                        [
                            new AiContentPart(
                                "text",
                                Text: """
                                You index shop diagrams, panel photos, P&IDs, and equipment labels for Sable techs.
                                Write a detailed but concrete description that another assistant can search later.
                                Include:
                                - What the image shows (panel, wiring, HMI, schematic, photo, etc.)
                                - Readable text, tags, WO#s, part numbers, PLC names, I/O labels
                                - Notable components, states (alarms, lights), and relationships in diagrams
                                - Any safety or procedure cues visible
                                Do not invent text you cannot read. Use short headed sections.
                                """),
                            new AiContentPart("image_url", ImageUrl: dataUrl)
                        ])
                ],
                cancellationToken)).Trim();

            if (string.IsNullOrWhiteSpace(description))
            {
                document.Status = "StoredOnly";
                document.IndexError = "Image stored, but the vision model returned an empty description.";
                return 0;
            }

            var searchable = $"""
                # Image: {document.Title}
                File: {document.FileName}
                Source: vision description (Gemini)

                {description}
                """;

            var chunks = ChunkText(searchable);
            AddChunks(document.Id, chunks);
            document.Status = "Indexed";
            document.IndexError = null;
            return chunks.Count;
        }
        catch (Exception ex)
        {
            document.Status = "StoredOnly";
            document.IndexError =
                $"Image stored, but vision indexing failed: {TruncateError(ex.Message)}";
            return 0;
        }
    }

    private void AddChunks(Guid documentId, IReadOnlyList<string> chunks)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            _db.Add(new KnowledgeChunk
            {
                DocumentId = documentId,
                Ordinal = i,
                Text = chunks[i]
            });
        }
    }

    private static string TruncateError(string message) =>
        message.Length <= 400 ? message : message[..400] + "…";

    private static bool IsZip(string fileName, string contentType)
    {
        if (string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var type = contentType.ToLowerInvariant();
        return type is "application/zip" or "application/x-zip-compressed" or "multipart/x-zip";
    }

    private static bool IsIndexableFile(string fileName, string? contentType)
    {
        if (IsPdfFile(fileName, contentType))
        {
            return true;
        }

        var ext = Path.GetExtension(fileName);
        if (IndexableExtensions.Contains(ext))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var type = contentType.ToLowerInvariant();
        return type.StartsWith("text/", StringComparison.Ordinal) ||
               type is "application/json" or "application/xml";
    }

    private static bool IsImageFile(string fileName, string? contentType)
    {
        if (ImageExtensions.Contains(Path.GetExtension(fileName)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(contentType) &&
               contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlcProgramFile(string fileName) =>
        PlcProgramExtensions.Contains(Path.GetExtension(fileName));

    private static bool IsPdfFile(string fileName, string? contentType)
    {
        if (PdfExtensions.Contains(Path.GetExtension(fileName)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(contentType) &&
               contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeImageMediaType(string fileName, string contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
        {
            return contentType.Split(';')[0].Trim().ToLowerInvariant();
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/jpeg"
        };
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
            ".md" or ".markdown" => "text/markdown",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            ".xml" or ".l5x" or ".l5k" => "application/xml",
            ".csv" => "text/csv",
            ".pdf" => "application/pdf",
            ".log" or ".st" or ".scl" or ".awl" => "text/plain",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };

    public async Task DeleteAsync(
        Guid organizationId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var document = _db.KnowledgeDocuments.FirstOrDefault(d =>
                           d.Id == documentId && d.OrganizationId == organizationId)
                       ?? throw new InvalidOperationException("Knowledge document was not found.");

        var chunks = _db.KnowledgeChunks.Where(c => c.DocumentId == documentId).ToList();
        foreach (var chunk in chunks)
        {
            _db.Remove(chunk);
        }

        if (_blobs.IsConfigured && !string.IsNullOrWhiteSpace(document.BlobPath))
        {
            try
            {
                await _blobs.DeleteAsync(document.BlobPath, cancellationToken);
            }
            catch
            {
                // Still remove SQL rows if blob delete fails.
            }
        }

        _db.Remove(document);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            organizationId,
            "knowledge.document_deleted",
            null,
            nameof(KnowledgeDocument),
            documentId,
            cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<KnowledgeExcerptDto>> SearchAsync(
        Guid organizationId,
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<KnowledgeExcerptDto>>([]);
        }

        var docs = _db.KnowledgeDocuments
            .Where(d => d.OrganizationId == organizationId && d.Status == "Indexed")
            .ToList()
            .ToDictionary(d => d.Id);

        if (docs.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<KnowledgeExcerptDto>>([]);
        }

        var docIds = docs.Keys.ToHashSet();
        var scored = _db.KnowledgeChunks
            .Where(c => docIds.Contains(c.DocumentId))
            .ToList()
            .Select(c =>
            {
                var score = Score(c.Text, terms);
                return (Chunk: c, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.Ordinal)
            .Take(Math.Clamp(limit, 1, 12))
            .Select(x =>
            {
                var doc = docs[x.Chunk.DocumentId];
                return new KnowledgeExcerptDto(
                    doc.Id,
                    doc.Title,
                    doc.FileName,
                    x.Chunk.Ordinal,
                    TrimExcerpt(x.Chunk.Text),
                    x.Score);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<KnowledgeExcerptDto>>(scored);
    }

    private static KnowledgeDocumentDto Map(KnowledgeDocument d, int chunkCount) =>
        new(
            d.Id,
            d.Title,
            d.FileName,
            d.ContentType,
            d.ByteSize,
            d.Status,
            d.IndexError,
            chunkCount,
            d.CreatedAt,
            d.UpdatedAt);

    private static bool TryExtractText(
        string fileName,
        string contentType,
        Stream content,
        out string text)
    {
        text = string.Empty;
        var ext = Path.GetExtension(fileName);
        var type = contentType.ToLowerInvariant();
        var looksText =
            IndexableExtensions.Contains(ext) ||
            type.StartsWith("text/", StringComparison.Ordinal) ||
            type is "application/json" or "application/xml";

        if (!looksText)
        {
            return false;
        }

        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        text = reader.ReadToEnd();
        text = text.Replace("\0", string.Empty).Trim();
        return text.Length > 0;
    }

    private static bool TryExtractPdfText(Stream content, out string text)
    {
        text = string.Empty;
        string? tempPath = null;
        Stream? ownedStream = null;
        try
        {
            Stream readStream = content;
            if (content.CanSeek)
            {
                content.Position = 0;
            }
            else
            {
                tempPath = Path.Combine(Path.GetTempPath(), $"palantir-pdf-{Guid.NewGuid():N}");
                using (var fs = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    content.CopyTo(fs);
                }

                ownedStream = new FileStream(
                    tempPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                readStream = ownedStream;
            }

            using var document = PdfDocument.Open(readStream);
            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                var pageText = page.Text?.Trim();
                if (string.IsNullOrWhiteSpace(pageText))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine().AppendLine();
                }

                sb.AppendLine($"--- Page {page.Number} ---");
                sb.AppendLine(pageText);
            }

            text = sb.ToString().Trim();
            return text.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            ownedStream?.Dispose();
            if (content.CanSeek)
            {
                content.Position = 0;
            }

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

    internal static List<string> ChunkText(string text, int maxChars = 900, int overlap = 120)
    {
        var normalized = Regex.Replace(text, @"\r\n?", "\n").Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        if (normalized.Length <= maxChars)
        {
            return [normalized];
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < normalized.Length)
        {
            var length = Math.Min(maxChars, normalized.Length - start);
            var end = start + length;
            if (end < normalized.Length)
            {
                var window = normalized[start..end];
                var breakAt = Math.Max(
                    window.LastIndexOf("\n\n", StringComparison.Ordinal),
                    window.LastIndexOf('\n'));
                if (breakAt < maxChars / 3)
                {
                    breakAt = window.LastIndexOf(' ');
                }

                if (breakAt > maxChars / 3)
                {
                    end = start + breakAt;
                }
            }

            var slice = normalized[start..end].Trim();
            if (slice.Length > 0)
            {
                chunks.Add(slice);
            }

            if (end >= normalized.Length)
            {
                break;
            }

            start = Math.Max(end - overlap, start + 1);
        }

        return chunks;
    }

    private static List<string> Tokenize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return Regex.Matches(input.ToLowerInvariant(), @"[a-z0-9][a-z0-9\-]{1,}")
            .Select(m => m.Value)
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .Distinct()
            .Take(24)
            .ToList();
    }

    private static double Score(string text, IReadOnlyList<string> terms)
    {
        var lower = text.ToLowerInvariant();
        double score = 0;
        foreach (var term in terms)
        {
            var idx = 0;
            var hits = 0;
            while ((idx = lower.IndexOf(term, idx, StringComparison.Ordinal)) >= 0)
            {
                hits++;
                idx += term.Length;
                if (hits > 8)
                {
                    break;
                }
            }

            if (hits > 0)
            {
                score += hits * (term.Length >= 5 ? 1.4 : 1.0);
            }
        }

        return score;
    }

    private static string TrimExcerpt(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= 500 ? flat : flat[..500] + "…";
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "can", "had", "her", "was", "one",
        "our", "out", "has", "have", "been", "from", "they", "with", "this", "that", "what", "when",
        "where", "which", "while", "about", "into", "than", "then", "them", "these", "those", "will",
        "would", "could", "should", "their", "there", "here", "just", "like", "also", "only"
    };
}
