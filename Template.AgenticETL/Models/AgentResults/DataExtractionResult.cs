using System.Text.Json.Serialization;

namespace Template.AgenticETL.Models.AgentResults;

public sealed record DataExtractionResult(
    [property: JsonPropertyName("extractedData")] Dictionary<string, string> ExtractedData,
    [property: JsonPropertyName("description")] string Description);