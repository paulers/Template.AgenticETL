using Foundatio.Jobs;
using Foundatio.Queues;
using Microsoft.Extensions.Options;
using Template.AgenticETL.Models;
using Template.AgenticETL.Models.ConfigBindings;

namespace Template.AgenticETL.Services;

public class DocumentProcessingQueueJob : QueueJobBase<DocumentWorkItem>
{
    private IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _lockRenewalInterval;

    public DocumentProcessingQueueJob(
        IQueue<DocumentWorkItem> queue,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory serviceScopeFactory,
        IOptions<DocumentProcessingOptions> options) : base(queue, loggerFactory: loggerFactory)
    {
        _scopeFactory = serviceScopeFactory;
        _lockRenewalInterval = options.Value.LockRenewalInterval;
    }

    protected override async Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<DocumentWorkItem> context)
    {
        var workItem = context.QueueEntry.Value;

        _logger.LogInformation("Processing document work item: {WorkItemId}", workItem.WorkItemId);

        try
        {
            // Both operations follow host cancellation. Separate sources let us stop the heartbeat
            // after processing finishes, or request processing cancellation if lease renewal fails.
            using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

            // Start the async renewal loop without awaiting it here, so it can renew the lease
            // while ProcessJob is waiting on parsing, model calls, or repository updates.
            var heartbeat = RenewLockWhileProcessingAsync(context, processingCancellation, heartbeatCancellation.Token);

            JobResult result;
            try
            {
                result = await ProcessJob(workItem, processingCancellation.Token);
            }
            finally
            {
                // Always stop the heartbeat, including when processing throws. Await it so any
                // in-flight renewal finishes and renewal errors reach the outer catch before
                // Foundatio acknowledges or retries the entry.
                await heartbeatCancellation.CancelAsync();
                await heartbeat;
            }

            // Do not return success if cancellation was requested near the end of processing.
            processingCancellation.Token.ThrowIfCancellationRequested();
            return result;
        } catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process work item {WorkItemId}", workItem.WorkItemId);
            return JobResult.FromException(ex);
        }
    }

    private async Task RenewLockWhileProcessingAsync(
        QueueEntryContext<DocumentWorkItem> context,
        CancellationTokenSource processingCancellation,
        CancellationToken heartbeatCancellation)
    {
        // Renew on a timer rather than between workflow stages, since a single model call
        // can outlast the queue lease. Awaiting each renewal prevents overlapping renewals.
        using var timer = new PeriodicTimer(_lockRenewalInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(heartbeatCancellation))
            {
                heartbeatCancellation.ThrowIfCancellationRequested();
                // Extend the queue entry's lease and any associated processing lock.
                await context.RenewLockAsync();
            }
        }
        catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
        {
            // Expected when processing finishes or the host shuts down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to renew the queue lease for work item {WorkItemId}. Canceling processing.",
                context.QueueEntry.Value.WorkItemId);
            // We can no longer rely on owning the lease. Ask the workflow to stop cooperatively,
            // then propagate the renewal error so this attempt is reported as failed.
            await processingCancellation.CancelAsync();
            throw;
        }
    }

    private async Task<JobResult> ProcessJob(DocumentWorkItem? workItem, CancellationToken ct)
    {
        if (workItem is null)
        {
            _logger.LogWarning("Received null work item.");
            return JobResult.FromException(new ArgumentNullException(nameof(workItem)));
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var openAIOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<OpenAIOptions>>();

        try
        {
            // Start each attempt with fresh progress and results. In particular, an earlier
            // failure must not remain visible while this retry processes successfully.
            await repository.SetProcessingStatus(workItem.WorkItemId, ct);

            // instantiate the workflow
            var workflow = new DocumentProcessingWorkflow(openAIOptions);

            _logger.LogInformation("Starting workflow for file: {FileName}", workItem.FileName);

            // execute the workflow
            await workflow.ExecuteWorkflow(workItem.WorkItemId, workItem.DocumentInBase64, repository, ct);

            // Check cancellation before saving completion, even if the workflow returned normally
            // after a cancellation request. This also covers cancellation after lease renewal fails.
            ct.ThrowIfCancellationRequested();
            await repository.SetCompletedStatus(workItem.WorkItemId, ct);

            // return success to ack the message
            return JobResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Processing for file {FileName} with work item ID {WorkItemId} was canceled.", workItem.FileName, workItem.WorkItemId);
            return JobResult.FromException(new OperationCanceledException("Processing was canceled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process file {FileName} with work item ID {WorkItemId}", workItem.FileName, workItem.WorkItemId);
            // Include InvalidOperationException from workflow errors here. A separate catch
            // previously returned failure without updating the document's persisted status.
            await repository.SetErroredStatus(workItem.WorkItemId, ex.Message, ct);
            return JobResult.FromException(ex);
        }
    }
}
