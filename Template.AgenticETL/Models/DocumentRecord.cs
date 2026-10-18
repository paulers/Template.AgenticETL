using System.Text.Json.Serialization;

namespace Template.AgenticETL.Models;

public class DocumentRecord
{
    public required string Id { get; set; }
    // Processing metadata
    public DateTime CreatedAt { get; set; }
    public DateTime? ParsedAt { get; set; }
    public DateTime? ClassifiedAt { get; set; }
    public DateTime? ExtractedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public DateTime? ErroredAt { get; set; }
    public string? ErrorMessage { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DocumentProcessingStatus Status { get; set; } = DocumentProcessingStatus.Created;

    // Document metadata
    public required string Name { get; set; }
    public string? Classification { get; set; }
    public double? ClassificationConfidence { get; set; }
    public string? Description { get; set; }

    // Customer data
    public Dictionary<string, string> ExtractedData { get; set; } = new();
}

public enum DocumentProcessingStatus
{
    Created,
    Parsed,
    Classified,
    Extracted,
    Completed,
    Errored,
    // Appended to preserve the numeric values of existing statuses in stored records.
    Processing
}
