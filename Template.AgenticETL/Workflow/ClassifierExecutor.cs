using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using OpenAI;
using OpenAI.Chat;
using StackExchange.Redis.Availability;
using Template.AgenticETL.Models.AgentResults;
using Template.AgenticETL.Services;

namespace Template.AgenticETL.Workflow;

public sealed partial class ClassifierExecutor : Executor<string[], ClassificationResult>
{
    private readonly AIAgent _agent;

    private AgentSession? _session;

    public ClassifierExecutor(OpenAIClient client, string model, string id) : base(id)
    {
        _agent = client.GetChatClient(model).AsAIAgent(
            name: id,
            instructions: """
            You are a document classifier assistant. You will receive the text content of a document and your task is to classify it into one of the 
            predefined categories based on its content. 
            
            ## The categories are: 
            - Invoice
            - Receipt
            - Correspondence
            - Other

            If you don't know which category the document belongs to, classify it as "Other".

            ## Return Format:
            Return your response as valid JSON only, with no additional text, in the form:

            {
                "classification": "CategoryName",
                "confidence": 0.0
            }

            """,
            description: "Classifies documents based on their content. Returns the most likely classification and a confidence score for each classification.");
    }

    public override async ValueTask<ClassificationResult> HandleAsync(string[] message, IWorkflowContext context, CancellationToken ct = default)
    {
        _session ??= await _agent.CreateSessionAsync(ct);

        var documentBody = PageMerger.MergePages(message);

        // Only ask the model to generate classification fields, not the document text.
        var response = await _agent.RunAsync<ClassificationResponse>(
            documentBody,
            _session,
            serializerOptions: new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true
            },
            options: new AgentRunOptions
            {
                ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.Json
            },
            cancellationToken: ct);

        var classification = response.Result;
        return new ClassificationResult(
            classification.Classification,
            classification.Confidence,
            documentBody);
    }
}
