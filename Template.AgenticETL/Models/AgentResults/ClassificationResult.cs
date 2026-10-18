using System.Text.Json.Serialization;

namespace Template.AgenticETL.Models.AgentResults;

public sealed record ClassificationResult(
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("documentBody")] string DocumentBody);