namespace Template.AgenticETL.Models.ConfigBindings;

public class OpenAIOptions
{
    public required string ApiKey { get; set; }
    public string ClassificationModel { get; set; } = "gpt-5.6-terra";
    public string ExtractionModel { get; set; } = "gpt-6-luna";
}
