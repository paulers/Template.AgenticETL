using Foundatio.Queues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Template.AgenticETL.Models;
using Template.AgenticETL.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace Template.AgenticETL.Endpoints;

public class UploadDocumentEndpoint : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/upload", Handle)
            .WithSummary("Upload a document for processing")
            .DisableAntiforgery();
    }

    public record Response(string RecordId, string StatusUrl);

    private static async Task<Results<
            Accepted<Response>, 
            BadRequest<string>, 
            Conflict<string>, 
            StatusCodeHttpResult, 
            InternalServerError<string>>> Handle(
        IFormFile? file,
        [FromForm] string? metadata,
        [FromQuery] bool? reprocess,
        IQueue<DocumentWorkItem> queue,
        IDocumentRepository repository,
        ILogger<UploadDocumentEndpoint> logger,
        CancellationToken ct)
    {
        #region Validate inputs
        if (file == null || file.Length == 0)
        {
            return TypedResults.BadRequest("No file uploaded.");
        }
        #endregion

        #region Verify incoming file
        var allowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application/pdf"
        };

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf"
        };

        if (!allowedExtensions.Contains(ext) && !allowedContentTypes.Contains(file.ContentType))
        {
            logger.LogWarning("Unsupported file upload attempted. File: {FileName}, ContentType: {ContentType}", file.FileName, file.ContentType);
            return TypedResults.BadRequest("Invalid file type. Only PDF files are allowed.");
        }
        #endregion

        #region Get the Metadata
        DocumentMetadata? deserializedMetadata = null;
        if (!string.IsNullOrEmpty(metadata))
        {
            try
            {
                deserializedMetadata = JsonSerializer.Deserialize<DocumentMetadata>(metadata,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true
                    });
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to deserialize metadata.");
                return TypedResults.BadRequest("Invalid metadata format.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during metadata deserialization");
                return TypedResults.StatusCode(500);
            }
        }
        
        #endregion

        // If metadata is null, create a new instance with default values (they may send a type)
        if (deserializedMetadata is null) deserializedMetadata = new();

        // Override the filename in metadata with the actual uploaded file's name to ensure consistency
        deserializedMetadata.FileName = Path.GetFileNameWithoutExtension(file.FileName);
        // Also the date
        deserializedMetadata.UploadedAt = DateTime.UtcNow;

        // If reprocess is specified or is false, check for conflicts
        if (reprocess != true)
        {
            // Here, we're fetching by filename. This obviously assumes that the filename is unique.
            // If your uniqueness criteria are different, adjust accordingly.
            var exists = await repository.GetDocumentDuplicateExists(deserializedMetadata.FileName!, ct);
            if (exists)
            {
                logger.LogInformation("Conflict detected for document with metadata: {Metadata}", metadata);
                return TypedResults.Conflict("A document with the same metadata already exists.");
            }
        }

        #region Read file to memory
        // Okay for files up to 100mb, but for larger files will want to consider streaming to a
        // temporary file or directly to a storage service instead of loading into memory.
        // Should theoretically be okay for PDFs...
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var documentBytes = ms.ToArray();
        #endregion

        #region Validate PDF before creating a record or queueing work
        // The request token signals cancellation when the client disconnects. CopyToAsync
        // already observes it; this checkpoint also covers cancellation after copying finishes.
        // PdfPig's synchronous calls do not accept a token, so they cannot be interrupted.
        // ThrowIfCancellationRequested does nothing unless canceled, then throws
        // OperationCanceledException so execution unwinds and using scopes dispose resources.
        ct.ThrowIfCancellationRequested();
        try
        {
            // Opening checks the document structure without extracting every page's text.
            using var document = PdfDocument.Open(documentBytes);
            if (document.NumberOfPages <= 0)
                return TypedResults.BadRequest("The PDF must contain at least one page.");
        }
        catch (PdfDocumentEncryptedException ex)
        {
            logger.LogWarning(ex, "Unable to open encrypted PDF upload {FileName}.", file.FileName);
            return TypedResults.BadRequest("The PDF could not be decrypted. Upload a PDF that can be opened without a password.");
        }
        catch (PdfDocumentFormatException ex)
        {
            logger.LogWarning(ex, "Unreadable PDF upload {FileName}.", file.FileName);
            return TypedResults.BadRequest("The uploaded file is not a readable PDF or is corrupted.");
        }
        // Catch cancellation requested while PdfPig was opening the document, before
        // encoding the payload, creating a record, or queueing work.
        ct.ThrowIfCancellationRequested();
        var base64String = Convert.ToBase64String(documentBytes);
        #endregion

        #region Create a pending document record
        string recordId;
        try
        {
            recordId = await repository.CreatePendingDocumentRecordAsync(deserializedMetadata, ct);
        } catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to create pending document record.");
            return TypedResults.InternalServerError("Failed to create pending document record.");
        }
        #endregion

        #region Enqueue the work item
        var job = new DocumentWorkItem
        {
            DocumentContentType = file.ContentType,
            DocumentInBase64 = base64String,
            FileName = file.FileName,
            Metadata = deserializedMetadata,
            WorkItemId = recordId
        };

        try
        {
            await queue.EnqueueAsync(job, new QueueEntryOptions
            {
                UniqueId = recordId
            });
        }
        catch (QueueException ex)
        {
            logger.LogCritical(ex, "Failed to enqueue document work item due to queue exception for record {recordId}.", recordId);
            await repository.RemoveDocumentById(recordId, ct);
            return TypedResults.InternalServerError("Failed to enqueue document work item due to queue exception.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to enqueue document work item for record {recordId}.", recordId);
            await repository.RemoveDocumentById(recordId, ct);
            return TypedResults.InternalServerError("Failed to enqueue document work item.");
        }
        #endregion

        // Figure out a better way to compose this URL if the endpoint path changes in the future.
        // For now, hardcoding is fine.
        var statusUrl = $"/api/v1/documents/{recordId}/status";
        return TypedResults.Accepted(statusUrl, new Response(recordId, statusUrl));
    }
}
