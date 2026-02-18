using System.Threading.Channels;

namespace Meziantou.GitHubActionsTracing.Server;

internal sealed class WorkflowRunProcessingQueue : IWorkflowRunProcessingQueue
{
    private readonly Channel<WorkflowRunProcessingItem> _channel = Channel.CreateUnbounded<WorkflowRunProcessingItem>();

    public ValueTask EnqueueAsync(WorkflowRunProcessingItem item, CancellationToken cancellationToken)
    {
        return _channel.Writer.WriteAsync(item, cancellationToken);
    }

    public IAsyncEnumerable<WorkflowRunProcessingItem> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
