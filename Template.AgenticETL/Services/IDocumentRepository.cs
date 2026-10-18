using Foundatio.Caching;
using Template.AgenticETL.Models;

namespace Template.AgenticETL.Services;

public interface IDocumentRepository
{
    Task<bool> GetDocumentDuplicateExists(string filename, CancellationToken ct);
    Task<DocumentRecord?> GetDocumentById(string id, CancellationToken ct);
    Task<string> CreatePendingDocumentRecordAsync(DocumentMetadata metadata, CancellationToken ct);
    Task RemoveDocumentById(string recordId, CancellationToken ct);
    
    /// <summary>
    /// Sets the status and timestamp when the document is successfully parsed out of the PDF into text
    /// </summary>
    /// <param name="id">record ID</param>
    /// <param name="ct">cancellation token</param>
    /// <returns></returns>
    Task SetParsedStatus(string id, CancellationToken ct);
    Task SetProcessingStatus(string id, CancellationToken ct);
    Task SetDocumentClassification(string id, string classification, double confidence, CancellationToken ct);
    Task SetDocumentExtractedData(string id, Dictionary<string, string> extractedData, string description, CancellationToken ct);
    Task SetCompletedStatus(string id, CancellationToken ct);
    Task SetErroredStatus(string id, string errorMessage, CancellationToken ct);
}

public class DocumentRepository(ICacheClient cache) : IDocumentRepository
{
    private readonly string cacheKeyPrefix = "document-records:";

    public async Task<string> CreatePendingDocumentRecordAsync(DocumentMetadata metadata, CancellationToken ct)
    {
        var dto = new DocumentRecord
        {
            Id = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
            Status = DocumentProcessingStatus.Created,
            Name = metadata.FileName,
            ExtractedData = new Dictionary<string, string>()
            {
                { "UploadedAt", metadata.UploadedAt.ToString("o")  }
            }
        };

        // If type is provided, set it; otherwise, leave it null for the LLM to figure it out
        if (!string.IsNullOrEmpty(metadata.Type)) dto.Classification = metadata.Type;

        // Add it to a list of document filenames for dupechecking later
        // Optimally dupe checking would be done with an ID of the document the user passes in
        // or another unique identifier, but for now we'll just use the filename
        await cache.ListAddAsync($"{cacheKeyPrefix}filenames", dto.Name);

        // Set it in the cache with a key based on its new ID for status retrieval
        await cache.SetAsync($"{cacheKeyPrefix}{dto.Id}", dto);

        // If it errored, remove it (or no-op)
        await cache.ListRemoveAsync($"{cacheKeyPrefix}errored", dto.Id);

        return dto.Id;
    }

    public async Task<bool> GetDocumentDuplicateExists(string filename, CancellationToken ct)
    {
        var list = await cache.GetListAsync<string>($"{cacheKeyPrefix}filenames");
        return list?.Value.Contains(filename) ?? false;
    }

    public async Task<DocumentRecord?> GetDocumentById(string id, CancellationToken ct)
    {
        var record = await cache.GetAsync<DocumentRecord>($"{cacheKeyPrefix}{id}");
        return record?.Value;
    }

    public async Task RemoveDocumentById(string recordId, CancellationToken ct)
    {
        var existing = await cache.GetAsync<DocumentRecord>($"{cacheKeyPrefix}{recordId}");
        if (existing is null || !existing.HasValue || existing.Value is null) return;
        var dto = existing.Value;

        await cache.ListRemoveAsync($"{cacheKeyPrefix}filenames", dto.Name);

        await cache.RemoveAsync($"{cacheKeyPrefix}{recordId}");
    }

    public async Task SetProcessingStatus(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var record = await cache.GetAsync<DocumentRecord>($"{cacheKeyPrefix}{id}");
        if (record?.Value is null)
            throw new InvalidOperationException($"Document record with ID {id} not found.");

        var dto = record.Value;
        // Status endpoints describe the current attempt. Clear results from the previous attempt
        // before restarting, so a retry cannot display old extraction data or completion timestamps.
        dto.Status = DocumentProcessingStatus.Processing;
        dto.ParsedAt = null;
        dto.ClassifiedAt = null;
        dto.ExtractedAt = null;
        dto.CompletedAt = null;
        dto.ErroredAt = null;
        dto.ErrorMessage = null;
        dto.Classification = null;
        dto.ClassificationConfidence = null;
        dto.Description = null;

        // UploadedAt is upload metadata, not model output. Keep it across attempts while
        // discarding the previous attempt's fields. Do not merge old extraction results.
        var extractedData = new Dictionary<string, string>();
        if (dto.ExtractedData.TryGetValue("UploadedAt", out var uploadedAt))
            extractedData["UploadedAt"] = uploadedAt;
        dto.ExtractedData = extractedData;

        await cache.SetAsync($"{cacheKeyPrefix}{id}", dto);
        await cache.ListRemoveAsync($"{cacheKeyPrefix}errored", id);
    }

    public async Task SetCompletedStatus(string id, CancellationToken ct)
    {
        var record = await cache.GetAsync<DocumentRecord>($"{cacheKeyPrefix}{id}");
        if (record?.Value is null)
        {
            throw new InvalidOperationException($"Document record with ID {id} not found.");
        }
        record.Value.Status = DocumentProcessingStatus.Completed;
        record.Value.CompletedAt = DateTime.UtcNow;
        // A successful retry supersedes the previous failure, including its tracking-list entry.
        record.Value.ErroredAt = null;
        record.Value.ErrorMessage = null;
        await cache.SetAsync($"{cacheKeyPrefix}{id}", record.Value);
        await cache.ListRemoveAsync($"{cacheKeyPrefix}errored", id);
    }

    public async Task SetErroredStatus(string id, string errorMessage, CancellationToken ct)
    {
        var record = await cache.GetAsync<DocumentRecord>($"{cacheKeyPrefix}{id}");
        if (record?.Value is null)
        {
            throw new InvalidOperationException($"Document record with ID {id} not found.");
        }
        record.Value.Status = DocumentProcessingStatus.Errored;
        record.Value.ErroredAt = DateTime.UtcNow;
        record.Value.ErrorMessage = errorMessage;
        // An errored attempt must not also advertise successful completion.
        record.Value.CompletedAt = null;
        await cache.SetAsync($"{cacheKeyPrefix}{id}", record.Value);
        // Add it to a list of errored document IDs for tracking
        await cache.ListAddAsync($"{cacheKeyPrefix}errored", id);
    }

    public async Task SetParsedStatus(string id, CancellationToken ct)
    {
        var existing = await cache.GetAsync<DocumentRecord>($"{cacheKeyPrefix}{id}");
        // Missing records are failures, not successful saves of workflow progress.
        if (existing?.Value is null)
            throw new InvalidOperationException($"Document record with ID {id} not found.");

        var dto = existing.Value;

        dto.ParsedAt = DateTime.UtcNow;
        dto.Status = DocumentProcessingStatus.Parsed;

        await cache.SetAsync($"{cacheKeyPrefix}{id}", dto);
    }

    public async Task SetDocumentClassification(string id, string classification, double confidence, CancellationToken ct)
    {
        var existing = await cache.GetAsync<DocumentRecord>($"{cacheKeyPrefix}{id}");
        if (existing?.Value is null)
            throw new InvalidOperationException($"Document record with ID {id} not found.");

        var dto = existing.Value;

        dto.Classification = classification;
        dto.ClassificationConfidence = confidence;
        dto.ClassifiedAt = DateTime.UtcNow;
        dto.Status = DocumentProcessingStatus.Classified;

        await cache.SetAsync($"{cacheKeyPrefix}{id}", dto);
    }

    public async Task SetDocumentExtractedData(string id, Dictionary<string, string> extractedData, string description, CancellationToken ct)
    {
        var existing = await cache.GetAsync<DocumentRecord>($"{cacheKeyPrefix}{id}");
        if (existing?.Value is null)
            throw new InvalidOperationException($"Document record with ID {id} not found.");

        var dto = existing.Value;

        // Replace model output rather than merging it, so fields removed by a retry do not linger.
        // Copy the input so preserving upload metadata does not mutate the agent's response.
        var newData = new Dictionary<string, string>(extractedData);
        if (dto.ExtractedData.TryGetValue("UploadedAt", out var uploadedAt))
            newData["UploadedAt"] = uploadedAt;
        dto.ExtractedData = newData;
        // Description is generated alongside extractedData and must be saved in the same update.
        dto.Description = description;
        dto.ExtractedAt = DateTime.UtcNow;
        dto.Status = DocumentProcessingStatus.Extracted;

        await cache.SetAsync($"{cacheKeyPrefix}{id}", dto);
    }
}
