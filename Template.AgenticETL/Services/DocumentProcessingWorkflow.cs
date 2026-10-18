using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Options;
using OpenAI;
using Template.AgenticETL.Models.AgentResults;
using Template.AgenticETL.Models.ConfigBindings;
using Template.AgenticETL.Workflow;

namespace Template.AgenticETL.Services;

public class DocumentProcessingWorkflow(IOptionsMonitor<OpenAIOptions> options)
{
    public async Task ExecuteWorkflow(string recordId, string documentInBase64, IDocumentRepository repository, CancellationToken ct)
    {
        // create the client
        var client = new OpenAIClient(options.CurrentValue.ApiKey);

        // create the executors
        var documentExtractorExecutor = new DocumentParserExecutor("DocumentParserExecutor");
        var classifierExecutor = new ClassifierExecutor(client, options.CurrentValue.ClassificationModel, "ClassificationExecutor");
        var dataExtractorExecutor = new DataExtractorExecutor(client, options.CurrentValue.ExtractionModel, "DataExtractionExecutor");

        // define the workflow
        var workflow = new WorkflowBuilder(documentExtractorExecutor)
            .AddEdge(documentExtractorExecutor, classifierExecutor)
            .AddEdge(classifierExecutor, dataExtractorExecutor)
            .Build();

        // create the run in streaming format
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, documentInBase64, cancellationToken: ct);
        
        // handle events which occur during the workflow execution
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(ct))
        {
            switch (evt)
            {
                case ExecutorCompletedEvent completed:
                    if (completed.ExecutorId == documentExtractorExecutor.Id && completed.Data is string[] pages)
                    {
                        await repository.SetParsedStatus(recordId, ct);
                    }
                    else if (completed.ExecutorId == classifierExecutor.Id && completed.Data is ClassificationResult)
                    {
                        var data = completed.Data as ClassificationResult;
                        if (data is not null)
                            await repository.SetDocumentClassification(recordId, data.Classification, data.Confidence, ct);
                    }
                    else if (completed.ExecutorId == dataExtractorExecutor.Id && completed.Data is DataExtractionResult)
                    {
                        var data = completed.Data as DataExtractionResult;
                        if (data is not null)
                            await repository.SetDocumentExtractedData(recordId, data.ExtractedData, data.Description, ct);
                    }
                    break;

                case ExecutorFailedEvent failed:
                    // The queue job saves failures in one place for both executor and workflow errors.
                    // Include the underlying message so the persisted error remains useful.
                    throw new InvalidOperationException($"Executor '{failed.ExecutorId}' failed during execution: {failed.Data?.Message}", failed.Data as Exception);

                case WorkflowErrorEvent error:
                    // Bubble up the exception
                    throw new InvalidOperationException($"Document workflow failed.", error.Data as Exception);
            }
        }
    }
}
