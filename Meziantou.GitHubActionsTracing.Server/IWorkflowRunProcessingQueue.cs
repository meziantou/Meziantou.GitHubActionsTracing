namespace Meziantou.GitHubActionsTracing.Server;

internal interface IWorkflowRunProcessingQueue
{
    ValueTask EnqueueAsync(WorkflowRunProcessingItem item, CancellationToken cancellationToken);

    IAsyncEnumerable<WorkflowRunProcessingItem> DequeueAllAsync(CancellationToken cancellationToken);
}
