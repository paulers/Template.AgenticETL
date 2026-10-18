using System.Text.Json.Serialization;

namespace Template.AgenticETL.Models.AgentResults;

public sealed record ClassificationResponse(
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("confidence")] double Confidence);
