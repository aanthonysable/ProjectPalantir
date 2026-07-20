using System.IO.Compression;
using System.Security.Cryptography;
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
            .OrderBy(d => CollectionSortKey(d.Collection))
            .ThenBy(d => d.Collection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.FolderPath ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => PreferLeafTitle(d.Title, d.FileName), StringComparer.OrdinalIgnoreCase)
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

    public Task<KnowledgeLibraryDto> GetLibraryAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var docs = _db.KnowledgeDocuments
            .Where(d => d.OrganizationId == organizationId && d.Status != "Duplicate")
            .ToList();

        var chunkCounts = _db.KnowledgeChunks
            .Where(c => docs.Select(d => d.Id).Contains(c.DocumentId))
            .ToList()
            .GroupBy(c => c.DocumentId)
            .ToDictionary(g => g.Key, g => g.Count());

        var orderedDocs = docs
            .OrderBy(d => CollectionSortKey(d.Collection))
            .ThenBy(d => d.Collection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.FolderPath ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => PreferLeafTitle(d.Title, d.FileName), StringComparer.OrdinalIgnoreCase)
            .Select(d => Map(d, chunkCounts.GetValueOrDefault(d.Id)))
            .ToList();

        IReadOnlyList<KnowledgeCollectionDto> collections = docs
            .GroupBy(d => string.IsNullOrWhiteSpace(d.Collection) ? "General" : d.Collection)
            .OrderBy(g => CollectionSortKey(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KnowledgeCollectionDto(
                g.Key,
                g.Count(),
                g.Select(d => d.FolderPath)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToList()))
            .ToList();

        return Task.FromResult(new KnowledgeLibraryDto(collections, orderedDocs));
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
                var isPdf = IsPdfFile(entryName, contentType: null);
                var isPlc = IsPlcProgramFile(entryName);
                if (!isText && !isImage && !isPdf && !isPlc)
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
                        var docTitleLarge = BuildZipEntryTitle(titlePrefix, zipLabel, relativeLarge, entryName);

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
                var docTitle = BuildZipEntryTitle(titlePrefix, zipLabel, relative, entryName);

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
        ApplyBrowseClassification(document, bodySample: null);

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

        // Hash first so we can detect identical blobs even when filenames differ.
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        document.ContentHash = await ComputeSha256HexAsync(content, cancellationToken);
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        if (TryMarkAsDuplicateOfExisting(document))
        {
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
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
                    SetTagsAndBrowse(document, null);
                }
                else
                {
                    AddChunks(document.Id, chunks, document.Title, document.FileName);
                    SetTagsAndBrowse(document, pdfText);
                    document.Status = "Indexed";
                    document.IndexError = null;
                }
            }
            else
            {
                document.Status = "StoredOnly";
                document.IndexError =
                    "PDF stored. No extractable text was found — scanned/image-only PDFs are not indexed yet.";
                SetTagsAndBrowse(document, null);
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
                        SetTagsAndBrowse(document, null);
                    }
                    else
                    {
                        AddChunks(document.Id, chunks, document.Title, document.FileName);
                        SetTagsAndBrowse(document, text);
                        document.Status = "Indexed";
                        document.IndexError = null;
                    }
                }
                else
                {
                    document.Status = "StoredOnly";
                    document.IndexError = "Stored in blob, but this file type is not indexed yet.";
                    SetTagsAndBrowse(document, null);
                }
            }
        }

        document.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private bool TryMarkAsDuplicateOfExisting(KnowledgeDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.ContentHash))
        {
            return false;
        }

        var keeper = _db.KnowledgeDocuments
            .Where(d =>
                d.OrganizationId == document.OrganizationId &&
                d.Id != document.Id &&
                d.ContentHash == document.ContentHash &&
                d.Status != "Duplicate")
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .FirstOrDefault();

        if (keeper is null)
        {
            return false;
        }

        // Prefer older document; if this one is older, leave it and let periodic scan mark the other.
        if (document.CreatedAt < keeper.CreatedAt ||
            (document.CreatedAt == keeper.CreatedAt && document.Id.CompareTo(keeper.Id) < 0))
        {
            return false;
        }

        var existingChunks = _db.KnowledgeChunks.Where(c => c.DocumentId == document.Id).ToList();
        foreach (var chunk in existingChunks)
        {
            _db.Remove(chunk);
        }

        document.Status = "Duplicate";
        document.DuplicateOfDocumentId = keeper.Id;
        document.IndexError =
            $"Duplicate of “{PreferLeafTitle(keeper.Title, keeper.FileName)}” (same file contents).";
        SetTagsAndBrowse(document, null);
        return true;
    }

    public async Task<KnowledgeDedupResult> ScanAndMarkDuplicatesAsync(
        CancellationToken cancellationToken = default)
    {
        var hashesComputed = 0;
        var missingHash = _db.KnowledgeDocuments
            .Where(d => d.Status != "Duplicate" && (d.ContentHash == null || d.ContentHash == ""))
            .OrderBy(d => d.CreatedAt)
            .Take(40)
            .ToList();

        foreach (var doc in missingHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(doc.BlobPath) || !_blobs.IsConfigured)
            {
                continue;
            }

            try
            {
                await using var stream = await _blobs.OpenReadAsync(doc.BlobPath, cancellationToken);
                doc.ContentHash = await ComputeSha256HexAsync(stream, cancellationToken);
                doc.UpdatedAt = DateTimeOffset.UtcNow;
                hashesComputed++;
            }
            catch
            {
                // Leave hash null; retry next scan.
            }
        }

        if (hashesComputed > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        var duplicatesMarked = 0;
        var hashed = _db.KnowledgeDocuments
            .Where(d => d.Status != "Duplicate" && d.ContentHash != null && d.ContentHash != "")
            .ToList()
            .GroupBy(d => (d.OrganizationId, Hash: d.ContentHash!))
            .Where(g => g.Count() > 1);

        foreach (var group in hashed)
        {
            var ordered = group.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToList();
            var keeper = ordered[0];
            foreach (var dup in ordered.Skip(1))
            {
                if (dup.Status == "Duplicate" && dup.DuplicateOfDocumentId == keeper.Id)
                {
                    continue;
                }

                var chunks = _db.KnowledgeChunks.Where(c => c.DocumentId == dup.Id).ToList();
                foreach (var chunk in chunks)
                {
                    _db.Remove(chunk);
                }

                dup.Status = "Duplicate";
                dup.DuplicateOfDocumentId = keeper.Id;
                dup.IndexError =
                    $"Duplicate of “{PreferLeafTitle(keeper.Title, keeper.FileName)}” (same file contents).";
                dup.UpdatedAt = DateTimeOffset.UtcNow;
                duplicatesMarked++;
            }
        }

        if (duplicatesMarked > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new KnowledgeDedupResult(hashesComputed, duplicatesMarked);
    }

    private static async Task<string> ComputeSha256HexAsync(Stream content, CancellationToken cancellationToken)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            hasher.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
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
            AddChunks(document.Id, chunks, document.Title, document.FileName);
            SetTagsAndBrowse(document, description);
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

    private void AddChunks(
        Guid documentId,
        IReadOnlyList<string> chunks,
        string title,
        string fileName)
    {
        var ordinal = 0;
        var overview = BuildOverviewChunk(title, fileName, chunks.Count == 0 ? null : chunks[0]);
        if (!string.IsNullOrWhiteSpace(overview))
        {
            _db.Add(new KnowledgeChunk
            {
                DocumentId = documentId,
                Ordinal = ordinal++,
                Text = overview
            });
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            _db.Add(new KnowledgeChunk
            {
                DocumentId = documentId,
                Ordinal = ordinal++,
                Text = chunks[i]
            });
        }
    }

    private static string BuildOverviewChunk(string title, string fileName, string? firstChunk)
    {
        var leaf = PreferLeafTitle(title, fileName);
        var tags = BuildTags(title, fileName, firstChunk);
        var sb = new StringBuilder();
        sb.AppendLine($"Document: {leaf}");
        if (!string.Equals(leaf, title, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"Full title: {title}");
        }

        sb.AppendLine($"File: {fileName}");
        if (!string.IsNullOrWhiteSpace(tags))
        {
            sb.AppendLine($"Tags: {tags}");
        }

        return sb.ToString().Trim();
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

    public Task<KnowledgeDownloadDto> OpenDownloadAsync(
        Guid organizationId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (!_blobs.IsConfigured)
        {
            throw new InvalidOperationException("Azure Blob Storage is not configured.");
        }

        var document = _db.KnowledgeDocuments.FirstOrDefault(d =>
                           d.Id == documentId && d.OrganizationId == organizationId)
                       ?? throw new InvalidOperationException("Knowledge document was not found.");

        if (string.IsNullOrWhiteSpace(document.BlobPath))
        {
            throw new InvalidOperationException("Knowledge document has no stored file.");
        }

        return OpenDownloadCoreAsync(document, cancellationToken);
    }

    private async Task<KnowledgeDownloadDto> OpenDownloadCoreAsync(
        KnowledgeDocument document,
        CancellationToken cancellationToken)
    {
        var stream = await _blobs.OpenReadAsync(document.BlobPath, cancellationToken);
        var downloadName = Path.GetFileName(document.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(downloadName))
        {
            downloadName = PreferLeafTitle(document.Title, document.FileName);
            var ext = Path.GetExtension(document.FileName);
            if (!string.IsNullOrWhiteSpace(ext) &&
                !downloadName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                downloadName += ext;
            }
        }

        return new KnowledgeDownloadDto(
            stream,
            downloadName,
            string.IsNullOrWhiteSpace(document.ContentType)
                ? "application/octet-stream"
                : document.ContentType,
            PreferLeafTitle(document.Title, document.FileName));
    }

    private static KnowledgeDocumentDto Map(KnowledgeDocument d, int chunkCount) =>
        new(
            d.Id,
            PreferLeafTitle(d.Title, d.FileName),
            d.FileName,
            d.ContentType,
            d.ByteSize,
            d.Status,
            d.IndexError,
            d.Tags,
            string.IsNullOrWhiteSpace(d.Collection) ? "General" : d.Collection,
            d.FolderPath,
            d.ContentHash,
            d.DuplicateOfDocumentId,
            chunkCount,
            d.CreatedAt,
            d.UpdatedAt);

    public async Task<int> BackfillSearchTagsAsync(CancellationToken cancellationToken = default)
    {
        var docs = _db.KnowledgeDocuments.ToList();
        var updated = 0;
        foreach (var doc in docs)
        {
            var tags = BuildTags(doc.Title, doc.FileName, null);
            var (collection, folder) = ClassifyBrowseLocation(doc.Title, doc.FileName, tags, null);
            var folderNorm = string.IsNullOrWhiteSpace(folder) ? null : folder;
            if (string.Equals(doc.Tags, tags, StringComparison.Ordinal) &&
                string.Equals(doc.Collection, collection, StringComparison.Ordinal) &&
                string.Equals(doc.FolderPath ?? "", folderNorm ?? "", StringComparison.Ordinal))
            {
                continue;
            }

            doc.Tags = tags;
            doc.Collection = collection;
            doc.FolderPath = folderNorm;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            updated++;
        }

        if (updated > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return updated;
    }

    public Task<IReadOnlyList<(Guid Id, string Title, string FileName, string? Tags)>> ListIndexedCatalogAsync(
        Guid organizationId,
        int limit = 40,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 80);
        IReadOnlyList<(Guid Id, string Title, string FileName, string? Tags)> items = _db.KnowledgeDocuments
            .Where(d => d.OrganizationId == organizationId && d.Status == "Indexed")
            .ToList()
            .OrderByDescending(d => d.UpdatedAt)
            .Take(take)
            .Select(d => (d.Id, PreferLeafTitle(d.Title, d.FileName), d.FileName, d.Tags))
            .ToList();
        return Task.FromResult(items);
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
            .ToList();

        if (docs.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<KnowledgeExcerptDto>>([]);
        }

        // Rank documents by title / filename / tags first — zip PDFs are findable by path even
        // when body text is sparse or noisy.
        var rankedDocs = docs
            .Select(d =>
            {
                var meta = $"{PreferLeafTitle(d.Title, d.FileName)} {d.Title} {d.FileName} {d.Collection} {d.FolderPath} {d.Tags}";
                var metaScore = ScoreMeta(meta, terms);
                return (Doc: d, MetaScore: metaScore);
            })
            .OrderByDescending(x => x.MetaScore)
            .ToList();

        var strongDocs = rankedDocs.Where(x => x.MetaScore >= 2.5).Take(30).ToList();
        var candidateDocs = strongDocs.Count > 0
            ? strongDocs
            : rankedDocs.Take(20).ToList();

        var candidateIds = candidateDocs.Select(x => x.Doc.Id).ToHashSet();
        var metaById = candidateDocs.ToDictionary(x => x.Doc.Id, x => x.MetaScore);
        var docsById = candidateDocs.ToDictionary(x => x.Doc.Id, x => x.Doc);

        var scored = _db.KnowledgeChunks
            .Where(c => candidateIds.Contains(c.DocumentId))
            .ToList()
            .Select(c =>
            {
                var bodyScore = ScoreBody(c.Text, terms);
                var meta = metaById.GetValueOrDefault(c.DocumentId);
                // Title/tag hits dominate; body confirms relevance inside the doc.
                var score = bodyScore + meta * 2.2;
                return (Chunk: c, Score: score, Meta: meta);
            })
            .Where(x => x.Score > 0 && (x.Meta >= 1.5 || x.Score >= 2.5))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.Ordinal)
            .GroupBy(x => x.Chunk.DocumentId)
            .SelectMany(g => g.Take(2)) // up to 2 chunks per document
            .OrderByDescending(x => x.Score)
            .Take(Math.Clamp(limit, 1, 12))
            .Select(x =>
            {
                var doc = docsById[x.Chunk.DocumentId];
                return new KnowledgeExcerptDto(
                    doc.Id,
                    PreferLeafTitle(doc.Title, doc.FileName),
                    doc.FileName,
                    x.Chunk.Ordinal,
                    TrimExcerpt(x.Chunk.Text, maxChars: 700),
                    x.Score,
                    doc.Tags);
            })
            .ToList();

        // If filename/tags matched but body chunks missed (scanned-ish / sparse text), still surface a hit.
        if (scored.Count < Math.Min(limit, 4))
        {
            var have = scored.Select(s => s.DocumentId).ToHashSet();
            foreach (var hit in strongDocs.Where(d => !have.Contains(d.Doc.Id)).Take(Math.Min(limit, 4) - scored.Count))
            {
                scored.Add(new KnowledgeExcerptDto(
                    hit.Doc.Id,
                    PreferLeafTitle(hit.Doc.Title, hit.Doc.FileName),
                    hit.Doc.FileName,
                    0,
                    TrimExcerpt(
                        $"Matched knowledge document “{PreferLeafTitle(hit.Doc.Title, hit.Doc.FileName)}” " +
                        $"(tags: {hit.Doc.Tags ?? "n/a"}). Open/search this PDF in Admin → Knowledge for full text.",
                        maxChars: 400),
                    hit.MetaScore,
                    hit.Doc.Tags));
            }
        }

        IReadOnlyList<KnowledgeExcerptDto> result = scored
            .OrderByDescending(x => x.Score)
            .Take(Math.Clamp(limit, 1, 12))
            .ToList();
        return Task.FromResult(result);
    }

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

        var raw = Regex.Matches(input.ToLowerInvariant(), @"[a-z0-9][a-z0-9\-]{1,}")
            .Select(m => m.Value)
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .Distinct()
            .ToList();

        // Prefer significant terms so "how do I set up an ISO tank" → iso, tank
        var significant = raw.Where(t => t.Length >= 4 || SignificantShortTerms.Contains(t)).ToList();
        var chosen = significant.Count > 0 ? significant : raw;
        return chosen.Take(20).ToList();
    }

    private static double ScoreMeta(string text, IReadOnlyList<string> terms)
    {
        var lower = text.ToLowerInvariant().Replace('_', ' ').Replace('/', ' ').Replace('-', ' ');
        double score = 0;
        var matched = 0;
        foreach (var term in terms)
        {
            var hits = CountHits(lower, term, max: 4);
            if (hits <= 0)
            {
                continue;
            }

            matched++;
            var weight = term.Length >= 6 ? 3.2 : term.Length >= 4 ? 2.4 : 1.6;
            score += hits * weight;
        }

        if (matched >= 2)
        {
            score *= 1.35;
        }

        return score;
    }

    private static double ScoreBody(string text, IReadOnlyList<string> terms)
    {
        var lower = text.ToLowerInvariant();
        double score = 0;
        var matchedSignificant = 0;
        foreach (var term in terms)
        {
            var hits = CountHits(lower, term, max: 6);
            if (hits <= 0)
            {
                continue;
            }

            var weight = term.Length >= 6 ? 1.8 : term.Length >= 4 ? 1.2 : 0.35;
            if (term.Length >= 4 || SignificantShortTerms.Contains(term))
            {
                matchedSignificant++;
            }

            score += hits * weight;
        }

        // Avoid ranking a chunk that only matched filler short tokens.
        if (matchedSignificant == 0 && terms.Any(t => t.Length >= 4 || SignificantShortTerms.Contains(t)))
        {
            return 0;
        }

        return score;
    }

    private static int CountHits(string haystack, string term, int max)
    {
        var idx = 0;
        var hits = 0;
        while ((idx = haystack.IndexOf(term, idx, StringComparison.Ordinal)) >= 0)
        {
            hits++;
            idx += term.Length;
            if (hits >= max)
            {
                break;
            }
        }

        return hits;
    }

    private static string TrimExcerpt(string text, int maxChars = 500)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= maxChars ? flat : flat[..maxChars] + "…";
    }

    private static string BuildZipEntryTitle(
        string? titlePrefix,
        string zipLabel,
        string relativePath,
        string entryName)
    {
        var leaf = Path.GetFileNameWithoutExtension(entryName);
        if (string.IsNullOrWhiteSpace(leaf))
        {
            leaf = entryName;
        }

        var folder = Path.GetDirectoryName(relativePath.Replace('\\', '/'))?.Replace('\\', '/').Trim('/');
        // Prefer readable leaf; keep a short folder hint for disambiguation.
        string core;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var folderLeaf = folder.Contains('/')
                ? folder[(folder.LastIndexOf('/') + 1)..]
                : folder;
            core = string.IsNullOrWhiteSpace(folderLeaf) ||
                   folderLeaf.Equals(leaf, StringComparison.OrdinalIgnoreCase)
                ? leaf
                : $"{leaf} · {folderLeaf}";
        }
        else
        {
            core = leaf;
        }

        if (!string.IsNullOrWhiteSpace(titlePrefix))
        {
            core = $"{titlePrefix.Trim()} / {core}";
        }
        else if (!string.IsNullOrWhiteSpace(zipLabel) &&
                 !core.Contains(zipLabel, StringComparison.OrdinalIgnoreCase))
        {
            // Keep pack name lightly for context without drowning the leaf.
            core = $"{core} ({zipLabel})";
        }

        return core.Length <= 300 ? core : core[..300];
    }

    private static void SetTagsAndBrowse(KnowledgeDocument document, string? bodySample)
    {
        document.Tags = BuildTags(document.Title, document.FileName, bodySample);
        ApplyBrowseClassification(document, bodySample);
    }

    private static void ApplyBrowseClassification(KnowledgeDocument document, string? bodySample)
    {
        var (collection, folder) = ClassifyBrowseLocation(
            document.Title, document.FileName, document.Tags, bodySample);
        document.Collection = collection;
        document.FolderPath = string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    /// <summary>
    /// Auto-classify into a browsable pack from path + content signals (not filename alone).
    /// </summary>
    private static (string Collection, string? FolderPath) ClassifyBrowseLocation(
        string title,
        string fileName,
        string? tags,
        string? bodySample)
    {
        var path = (fileName ?? "").Replace('\\', '/').Trim('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? pathFolder = null;
        if (parts.Length >= 2)
        {
            // Drop leaf filename; keep intermediate folders.
            pathFolder = parts.Length == 2
                ? null
                : string.Join('/', parts.Skip(1).Take(parts.Length - 2));
        }

        var bodyHead = string.IsNullOrWhiteSpace(bodySample)
            ? ""
            : bodySample.Length <= 2500
                ? bodySample
                : bodySample[..2500];
        var haystack = $"{title} {fileName} {tags} {bodyHead}".ToLowerInvariant();

        var contentCollection = MatchContentCollection(haystack);
        if (contentCollection is not null)
        {
            return (contentCollection, CleanFolderPath(pathFolder) ?? InferFolderFromSignals(haystack));
        }

        if (parts.Length > 0)
        {
            var pack = CleanPackName(parts[0]);
            if (!string.IsNullOrWhiteSpace(pack) &&
                !pack.Equals("general", StringComparison.OrdinalIgnoreCase))
            {
                // Map noisy zip roots onto canonical collections when possible.
                var mapped = MatchContentCollection(pack.ToLowerInvariant());
                return (mapped ?? pack, CleanFolderPath(pathFolder));
            }
        }

        return ("General", CleanFolderPath(pathFolder));
    }

    private static string? MatchContentCollection(string haystack)
    {
        // First match wins — order matters (more specific first).
        (string Name, string[] Needles)[] rules =
        [
            ("Engine Harness", ["engine harness", "mercedes-detroit", "mercedes detroit", "caterpillar/drc",
                "wiring harness", "uem ", "detroit diesel"]),
            ("VFDs & Drives", ["variable frequency", "vfd", "danfoss", "nidec", "commander id", "atv630",
                "atv650", "gs20"]),
            ("Flow Meters", ["flow meter", "flow meters", "seametrics", "gem1200", "isoil", "isomag"]),
            ("Modbus & Sensing", ["modbus voltage", "voltage sensing", "n4via", "modbus"]),
            ("ISO Tanks", ["iso tank", "strap calc", "hoover strap", "strap chart"]),
            ("Tutorials & Setup", ["tutorial", "quick setup", "documentation-tutorials", "documentation tutorials"]),
            ("Asset Drawings", ["asset drawing", "drawings/", "schematic", "p&id", "pid diagram"]),
            ("PLC Programs", [".acd", ".l5x", ".l5k", "plc program", "studio 5000", "rslogix"]),
            ("Captured Notes", ["captured ops knowledge", "source: overview ask", "promoted from ask"])
        ];

        foreach (var (name, needles) in rules)
        {
            if (needles.Any(n => haystack.Contains(n, StringComparison.Ordinal)))
            {
                return name;
            }
        }

        return null;
    }

    private static string? InferFolderFromSignals(string haystack)
    {
        if (haystack.Contains("mercedes") || haystack.Contains("detroit"))
        {
            return "Mercedes-Detroit";
        }

        if (haystack.Contains("caterpillar") || haystack.Contains(" cat "))
        {
            return "Caterpillar";
        }

        if (haystack.Contains("danfoss"))
        {
            return "Danfoss";
        }

        if (haystack.Contains("nidec"))
        {
            return "Nidec";
        }

        return null;
    }

    private static string CleanPackName(string raw)
    {
        var name = raw.Replace('_', ' ').Trim();
        // Strip zip export suffixes like -20260717T193843Z-1-001
        name = Regex.Replace(name, @"-\d{8}t\d{6}z.*$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+", " ").Trim();
        if (name.Length == 0)
        {
            return "General";
        }

        // Title-ish casing for short packs
        if (name.Equals(name.ToUpperInvariant(), StringComparison.Ordinal) && name.Length <= 40)
        {
            name = string.Join(' ', name.Split(' ')
                .Select(w => w.Length <= 2
                    ? w.ToUpperInvariant()
                    : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        }

        return name.Length <= 120 ? name : name[..120];
    }

    private static string? CleanFolderPath(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var cleaned = folder.Replace('\\', '/').Trim('/');
        cleaned = Regex.Replace(cleaned, @"/+", "/");
        return string.IsNullOrWhiteSpace(cleaned) ? null
            : cleaned.Length <= 260 ? cleaned : cleaned[..260];
    }

    private static int CollectionSortKey(string? collection)
    {
        var name = string.IsNullOrWhiteSpace(collection) ? "General" : collection;
        // Prefer domain packs before catch-all General / Captured Notes.
        return name switch
        {
            "Engine Harness" => 10,
            "VFDs & Drives" => 20,
            "Flow Meters" => 30,
            "Modbus & Sensing" => 40,
            "ISO Tanks" => 50,
            "Tutorials & Setup" => 60,
            "Asset Drawings" => 70,
            "PLC Programs" => 80,
            "Captured Notes" => 90,
            "General" => 100,
            _ => 85
        };
    }

    private static string PreferLeafTitle(string title, string fileName)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Path.GetFileNameWithoutExtension(fileName.Replace('\\', '/')) is { Length: > 0 } n
                ? n
                : fileName;
        }

        // Old zip titles look like "PACK / path/to/File.pdf" — prefer the leaf file name.
        if (title.Contains('/') || title.Contains('\\'))
        {
            var fromFile = Path.GetFileNameWithoutExtension(fileName.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(fromFile) && fromFile.Length >= 3)
            {
                return fromFile;
            }

            var leaf = title.Replace('\\', '/');
            leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
            leaf = Path.GetFileNameWithoutExtension(leaf);
            if (!string.IsNullOrWhiteSpace(leaf))
            {
                return leaf;
            }
        }

        return title;
    }

    private static string BuildTags(string title, string fileName, string? bodySample)
    {
        var bag = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddTokens(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            var normalized = source.Replace('\\', '/').Replace('_', ' ').Replace('-', ' ');
            foreach (Match m in Regex.Matches(normalized.ToLowerInvariant(), @"[a-z0-9][a-z0-9]{1,}"))
            {
                var t = m.Value;
                if (t.Length < 3 || StopWords.Contains(t) || TagNoise.Contains(t))
                {
                    continue;
                }

                // Drop zip export timestamp crumbs like 20260717t193843z
                if (Regex.IsMatch(t, @"^\d{8}t\d{6}z?\d*$") || Regex.IsMatch(t, @"^\d{6,}$"))
                {
                    continue;
                }

                bag.Add(t);
            }

            foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var seg = Regex.Replace(segment.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
                if (seg.Length is >= 3 and <= 48 && !StopWords.Contains(seg))
                {
                    bag.Add(seg);
                }
            }
        }

        AddTokens(title);
        AddTokens(fileName);
        if (!string.IsNullOrWhiteSpace(bodySample))
        {
            // Headings / early text only — keep tags compact.
            var head = bodySample.Length <= 2500 ? bodySample : bodySample[..2500];
            foreach (Match m in Regex.Matches(head, @"(?m)^(?:#+\s+|[A-Z][A-Z0-9 /&\-]{6,80}$)"))
            {
                AddTokens(m.Value);
            }
        }

        var ordered = bag
            .OrderByDescending(t => t.Length)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Take(28)
            .ToList();
        var joined = string.Join(", ", ordered);
        return joined.Length <= 800 ? joined : joined[..800];
    }

    private static readonly HashSet<string> SignificantShortTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "iso", "vfd", "plc", "hmi", "pid", "acm", "mcm", "can", "mod", "gem", "wo", "pdf", "sop"
    };

    private static readonly HashSet<string> TagNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "documentation", "tutorials", "tutorial", "info", "file", "pdf", "doc", "docs",
        "zip", "pack", "folder", "copy", "final", "new", "old", "temp", "tmp",
        "t193843z", "t193837z", "t193830z", "t193831z", "t193828z", "1", "001"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "can", "had", "her", "was", "one",
        "our", "out", "has", "have", "been", "from", "they", "with", "this", "that", "what", "when",
        "where", "which", "while", "about", "into", "than", "then", "them", "these", "those", "will",
        "would", "could", "should", "their", "there", "here", "just", "like", "also", "only",
        "how", "do", "set", "up", "an", "is", "me", "my", "we", "am", "be", "of", "to", "in", "on",
        "at", "by", "or", "if", "so", "as", "use", "using", "used", "please", "help", "need", "get",
        "make", "want", "tell", "show", "find", "look", "looking", "does", "did", "give", "any",
        "some", "more", "most", "very", "into", "over", "under", "after", "before", "between",
        "explain", "describe", "know", "something", "thing", "things", "a", "i"
    };
}
