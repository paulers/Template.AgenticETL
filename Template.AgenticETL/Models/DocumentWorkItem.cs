namespace Template.AgenticETL.Models;

public class DocumentWorkItem
{
    public required string FileName { get; set; }
    public required string WorkItemId { get; set; }
    public required string DocumentInBase64 { get; set; }
    public string DocumentContentType { get; set; } = "application/pdf";
    public required DocumentMetadata Metadata { get; set; }
}

public class DocumentMetadata
{
    public string FileName { get; set; } = string.Empty;
    public string? Type { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}