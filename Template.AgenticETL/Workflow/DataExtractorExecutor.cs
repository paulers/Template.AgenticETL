using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using OpenAI;
using OpenAI.Chat;
using Template.AgenticETL.Models.AgentResults;

namespace Template.AgenticETL.Workflow;

public sealed partial class DataExtractorExecutor : Executor<ClassificationResult, DataExtractionResult>
{
    private readonly AIAgent _agent;

    private AgentSession? _session;

    public DataExtractorExecutor(OpenAIClient client, string model, string id) : base(id)
    {
        _agent = client.GetChatClient(model).AsAIAgent(
            name: id,
            instructions: """
            You are a data extraction expert. You will be given text content of a document and its classification. 
            Your task is to extract specific data from the document and then describe the document in a single sentence. 
            Read through the entire document and look for the following:

            - Customer Name: 
              - Could be a first, middle and last name. Example: John B. Adams
              - Could be just a first and last name. Example: John Adams
              - Could be a first name and last initial. Example: John A.
            - Customer Number
              - Always numerical and always 8 digits
            - Invoice Number:
              - Any documents classified as invoices are likely to have this number somewhere.
              - Always starts with a letter and is followed by 6 digits. Example: D123456

            Never invent data that does not exist. If you cannot find one of these items to extract, simply return null.
            Remember to always summarize the document, for example: "This is an invoice to John Adams for $2000 by ABC Corp."

            ## Return Format:
            Return your response as valid JSON only, with no additional text, in the form:

            {
              "extractedData": {
                "customerName": "John Adams",
                "customerNumber": "78841931",
                "invoiceNumber": "D123456"
              },
              "description": "Description of the document"
            }

            """,
            description: "Extracts data about the customer or the invoice from the document text.");
    }

    public override async ValueTask<DataExtractionResult> HandleAsync(ClassificationResult classificationResult, IWorkflowContext context, CancellationToken ct = default)
    {
        _session ??= await _agent.CreateSessionAsync(ct);

        string body = @$"
        - **Classification:** {classificationResult.Classification}
        - **Classification Confidence:** {classificationResult.Confidence}
        
        ## Document Body:
        {classificationResult.DocumentBody}
        ";

        var response = await _agent.RunAsync<DataExtractionResult>(
            body,
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

        return response.Result;
    }
}
